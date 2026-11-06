using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private readonly Dictionary<FieldSymbol, TemporaryGuard> _constructionFields = [];
    private readonly Dictionary<FieldSymbol, MirPlace> _constructionStates = [];
    private TemporaryGuard? _constructionBase;
    private readonly List<TemporaryGuard> _lifecycleFields = [];
    private readonly Dictionary<FieldSymbol, TemporaryGuard> _preparedBaseFields = [];
    private int _exceptionalFinalizerDepth;

    private void InitializeLifecycle()
    {
        if (_bound.Symbol.ContainingStruct is not { } owner ||
            _bound.Symbol.FunctionKind is not (FunctionKind.Constructor or FunctionKind.AllocationConstructor or FunctionKind.InstanceInitializer or FunctionKind.Destructor or FunctionKind.DestructorGlue))
            return;
        bool destroying = _bound.Symbol.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue;
        MirPlace receiver = (_receiver ??= Receiver(_types.PointerTo(owner))).Project(new MirDerefProjection());
        if (_bound.Symbol.FunctionKind != FunctionKind.AllocationConstructor && owner.BaseType is { } baseType && TypeFacts.GetCompleteDestructor(baseType) is { } baseDestructor)
        {
            _constructionBase = new(receiver.Project(new MirBaseProjection(baseType)), Temporary(BuiltinTypes.Bool, _functionSource), baseDestructor);
            _lifecycleFields.Add(_constructionBase);
            SetFlag(_constructionBase.Active, destroying, _functionSource);
        }
        if (_bound.Symbol.FunctionKind == FunctionKind.Constructor && owner.BaseType is { } preparedBase)
        {
            AddPreparedFields(preparedBase, receiver.Project(new MirBaseProjection(preparedBase)), preparedBase.Constructors.IsEmpty, _functionSource);
            _lifecycleFields.AddRange(_preparedBaseFields.Values);
        }
        foreach (FieldSymbol field in owner.Fields)
        {
            bool prepared = _bound.Symbol.FunctionKind == FunctionKind.Constructor && field.IsAllocatorField;
            if (TypeFacts.GetCompleteDestructor(field.Type) is not { } destructor)
            {
                if (TypeFacts.ContainsAtomicStorage(field.Type))
                {
                    MirPlace state = Temporary(BuiltinTypes.Bool, _functionSource);
                    _constructionStates[field] = state;
                    SetFlag(state, destroying || prepared, _functionSource);
                }
                continue;
            }
            var guard = new TemporaryGuard(DestructorPlace(receiver.Project(new MirFieldProjection(field)), field.Type),
                Temporary(BuiltinTypes.Bool, _functionSource), destructor);
            _constructionFields.Add(field, guard);
            _constructionStates.Add(field, guard.Active);
            _lifecycleFields.Add(guard);
            SetFlag(guard.Active, destroying || prepared, _functionSource);
        }
        _unwindTarget = _rethrowTarget = CleanupChain(_lifecycleFields, _unwind, _unwind, true, _functionSource);
    }

    private void LifecycleCompleted(FunctionSymbol completed)
    {
        if (_bound.Symbol.FunctionKind is not (FunctionKind.Constructor or FunctionKind.AllocationConstructor) ||
            _bound.Symbol.ContainingStruct is not { } owner) return;
        if (TypeIdentity.AreSame(completed.ContainingType, owner))
        {
            foreach ((FieldSymbol field, MirPlace flag) in _constructionStates)
                if (completed.FunctionKind == FunctionKind.Constructor ||
                    completed.FunctionKind == FunctionKind.InstanceInitializer && field.HasInitializer)
                    SetFlag(flag, true, _functionSource);
            if (completed.FunctionKind == FunctionKind.Constructor && _constructionBase is { } baseGuard)
                SetFlag(baseGuard.Active, true, _functionSource);
        }
        else if (completed.FunctionKind == FunctionKind.InstanceInitializer && completed.ContainingStruct is { } initializedBase)
        {
            foreach (FieldSymbol field in initializedBase.Fields)
                if (field.HasInitializer && _preparedBaseFields.TryGetValue(field, out TemporaryGuard? guard))
                    SetFlag(guard.Active, true, _functionSource);
        }
        else if (_constructionBase is { } baseGuard && TypeIdentity.AreSame(completed.ContainingType, owner.BaseType))
            SetFlag(baseGuard.Active, true, _functionSource);
    }

    private void AddPreparedFields(StructTypeSymbol type, MirPlace place, bool initializesBaseFields, MirSourceInfo source)
    {
        if (type.BaseType is { } parent) AddPreparedFields(parent, place.Project(new MirBaseProjection(parent)), initializesBaseFields, source);
        foreach (FieldSymbol field in type.Fields)
            if ((field.IsAllocatorField || initializesBaseFields && field.HasInitializer) && TypeFacts.GetCompleteDestructor(field.Type) is { } destructor)
            {
                var guard = new TemporaryGuard(DestructorPlace(place.Project(new MirFieldProjection(field)), field.Type), Temporary(BuiltinTypes.Bool, source), destructor);
                SetFlag(guard.Active, field.IsAllocatorField, source);
                _preparedBaseFields.Add(field, guard);
            }
    }

    private void DestroyFields(MirSourceInfo source)
    {
        Block after = NewBlock();
        Jump(CleanupChain(_lifecycleFields, after, _unwind, _exceptionalFinalizerDepth > 0, source));
        _current = after;
    }
}
