using System.Collections.Immutable;
using Xenon.Compiler.Diagnostics;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;

namespace Xenon.Compiler.Mir.Analysis;

/// <summary>Readonly capability checks over executable MIR and shared dataflow.</summary>
internal sealed partial class MirReadonlyAnalysis(FunctionSymbol function, DiagnosticBag diagnostics,
    TextLocation fallbackLocation, IReadOnlyDictionary<FunctionSymbol, MirFunction> bodies,
    ImmutableArray<StructTypeSymbol> types, CancellationToken cancellationToken)
{
    private readonly object _hidden = new(), _external = new();
    private MemoryState _memory = new();
    private readonly HashSet<object> _summaryLocations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, Dictionary<FieldSymbol, object>> _fields = new(ReferenceEqualityComparer.Instance);
    private readonly EvaluationContext _rootContext = new();
    private EvaluationContext _context = null!;
    private readonly Dictionary<object, UncertainLocation> _uncertainLocations = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<FunctionSymbol, RecursiveFrame> _activeCalls = [];
    private readonly HashSet<(TextLocation Location, string Message)> _reported = [];
    private readonly Dictionary<object, MirEffectSite> _sites = new(ReferenceEqualityComparer.Instance);
    private MirFunction _body = null!;
    private int _loopDepth;
    private TextLocation _location = fallbackLocation;
    private sealed record MirEffectSite(MirSourceInfo Source, TypeSymbol Type);

    private MirEffectSite Site(object node, MirSourceInfo source, TypeSymbol type)
    {
        if (!_sites.TryGetValue(node, out var site)) _sites.Add(node, site = new(source, type));
        return site;
    }

    public void Analyze(MirFunction body)
    {
        _context = _rootContext;
        _context.Receiver.Add(_hidden);
        foreach (ParameterSymbol parameter in function.Parameters)
            if (ContainsAccess(parameter.Type) || parameter.Type is IFieldStorageTypeSymbol)
                StoreValue([Root(parameter)], [IsMutableParameter(parameter.Type) ? _external : _hidden], parameter.Type);
        Flow result = Visit(body);
        if (result.Return is { } returned && _context.ReturnSite is { } site)
        {
            _memory = returned;
            if (HasHiddenAccess(_context.Returned, function.ReturnType))
                Report(site, "cannot return a mutable capability obtained from hidden state", DiagnosticIds.MutableCapabilityReturn);
        }
    }

    private MirLocal Local(MirLocalId id) => _body.Locals.First(local => local.Id == id);
    private object LocalRoot(MirLocalId id) => Root((object?)Local(id).Variable ?? Local(id));
    private TypeSymbol PlaceType(MirPlace place) => Address(place).Type;
    private (HashSet<object> Storage, TypeSymbol Type) Address(MirPlace place)
    {
        HashSet<object> storage = [LocalRoot(place.Local)];
        TypeSymbol type = Local(place.Local).Type;
        foreach (MirProjection projection in place.Projections)
            switch (projection)
            {
                case MirFieldProjection field: storage = Project(storage, field.Field); type = field.Field.Type; break;
                case MirBaseProjection parent: type = parent.BaseType; break;
                case MirDerefProjection:
                    storage = Read(storage, type); type = ElementType(type) ?? type; break;
                case MirLifetimeProjection when type is LifetimeModifierTypeSymbol lifetime: type = lifetime.ElementType; break;
                case MirOwnerStorageProjection when type is OwnershipTypeSymbol owner: type = owner.StorageType; break;
                case MirAtomicStorageProjection when type is AtomicTypeSymbol atomic: type = atomic.ElementType; break;
                case MirIndexProjection index:
                    storage = type is ArrayTypeSymbol ? ArrayElements(Read(storage, type), index.Indices) : Uncertain(Read(storage, type));
                    type = ElementType(type) ?? type; break;
                case MirLinearIndexProjection index:
                    storage = ArrayElements(Read(storage, type), [index.Index]); type = ElementType(type) ?? type; break;
            }
        return (storage, type);
    }

    private HashSet<object> Operand(MirOperand operand) => operand switch
    {
        MirCopy copy => Read(Address(copy.Place).Storage, copy.Type),
        MirMove move => Read(Address(move.Place).Storage, move.Type),
        MirConstant constant when constant.Value is not null && ContainsAccess(constant.Type) => [_hidden],
        _ => [],
    };

    private HashSet<object> Value(MirRValue value, MirEffectSite site)
    {
        switch (value)
        {
            case MirDefault { Type: StructTypeSymbol structure }:
                HashSet<object> fresh = [Root(site)];
                if (_loopDepth != 0) _summaryLocations.UnionWith(fresh);
                StoreValue(fresh, [], structure);
                StoreReceiverTypes(fresh, [structure]);
                return fresh;
            case MirUse use: return Operand(use.Operand);
            case MirBorrow borrow: return Address(borrow.Place).Storage;
            case MirCast cast: return Operand(cast.Operand);
            case MirAtomicValue atomic: return Operand(atomic.Value);
            case MirStaticFieldAddress: return [_hidden];
            case MirExceptionReference: return [_external];
            case MirUnary unary: return ContainsAccess(unary.Type) ? Operand(unary.Operand) : [];
            case MirBinary binary:
                var result = Operand(binary.Left); result.UnionWith(Operand(binary.Right));
                return binary.Type is PointerTypeSymbol ? Uncertain(result) : ContainsAccess(binary.Type) ? result : [];
            case MirInterfaceView view:
                if (!_context.InterfaceValues.TryGetValue(site, out var instance))
                    _context.InterfaceValues.Add(site, instance = new(view.SourceType));
                if (_loopDepth != 0) _summaryLocations.Add(instance);
                var source = Operand(view.Address);
                StoreReceiverTypes([instance], KnownReceiverTypes(source));
                Store([instance], source, strong: true);
                return [instance];
            case MirStackAllocation:
                object allocated = Root(site); if (_loopDepth != 0) _summaryLocations.Add(allocated); return [allocated];
            case MirAggregate aggregate when aggregate.Type is IFieldStorageTypeSymbol aggregateType:
                HashSet<object> target = [Root(site)];
                for (int index = 0; index < Math.Min(aggregate.Fields.Length, aggregateType.AllInstanceFields.Length); index++)
                    StoreValue(Project(target, aggregateType.AllInstanceFields[index]), Operand(aggregate.Fields[index]), aggregateType.AllInstanceFields[index].Type);
                return target;
            default: return [];
        }
    }

    private void Assign(MirPlace place, HashSet<object> value, MirEffectSite site, bool check)
    {
        var target = Address(place);
        if (check)
        {
            CheckWrite(target.Storage, site);
            if (target.Storage.Contains(_external) && ExposesWritableAccess(target.Type) && HasHiddenAccess(value, target.Type))
                Report(site, "cannot store a mutable capability obtained from hidden state through an output parameter", DiagnosticIds.MutableCapabilityOutputEscape);
        }
        StoreValue(target.Storage, value, target.Type);
    }

    private int? ConstantIndex(MirOperand operand) => ConstantIndex(operand, []);
    private int? ConstantIndex(MirOperand operand, HashSet<MirLocalId> seen)
    {
        if (operand is MirConstant constant)
            return constant.Value switch { int n => n, long n when n is >= int.MinValue and <= int.MaxValue => (int)n,
                ulong n when n <= int.MaxValue => (int)n, uint n when n <= int.MaxValue => (int)n, _ => null };
        if (operand is not MirCopy { Place.Projections.IsEmpty: true } copy || !seen.Add(copy.Place.Local)) return null;
        MirAssign[] writes = _body.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.Destination.Local == copy.Place.Local).ToArray();
        return writes.Length == 1 ? writes[0].Value switch
        {
            MirUse use => ConstantIndex(use.Operand, seen), MirCast cast => ConstantIndex(cast.Operand, seen), _ => null,
        } : null;
    }
}