using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;

namespace Xenon.Compiler.Mir.Lowering;

public sealed partial class MirLowerer
{
    private readonly Dictionary<FieldSymbol, TemporaryGuard> _constructionFields = [];
    private readonly Dictionary<FieldSymbol, MirPlace> _constructionStates = [];
    private TemporaryGuard? _constructionBase;
    private readonly List<TemporaryGuard> _lifecycleFields = [];
    private int _exceptionalFinalizerDepth;

    private void InitializeLifecycle()
    {
        if (_bound.Symbol.ContainingStruct is not { } owner ||
            _bound.Symbol.FunctionKind is not (FunctionKind.Constructor or FunctionKind.InstanceInitializer or FunctionKind.Destructor or FunctionKind.DestructorGlue))
            return;
        bool destroying = _bound.Symbol.FunctionKind is FunctionKind.Destructor or FunctionKind.DestructorGlue;
        MirPlace receiver = (_receiver ??= Receiver(_types.PointerTo(owner))).Project(new MirDerefProjection());
        if (owner.BaseType is { } baseType && TypeFacts.GetCompleteDestructor(baseType) is { } baseDestructor)
        {
            _constructionBase = new(receiver.Project(new MirBaseProjection(baseType)), Temporary(BuiltinTypes.Bool, _functionSource), baseDestructor);
            _lifecycleFields.Add(_constructionBase);
            SetFlag(_constructionBase.Active, destroying, _functionSource);
        }
        foreach (FieldSymbol field in owner.Fields)
        {
            if (TypeFacts.GetCompleteDestructor(field.Type) is not { } destructor)
            {
                if (TypeFacts.ContainsAtomicStorage(field.Type))
                {
                    MirPlace state = Temporary(BuiltinTypes.Bool, _functionSource);
                    _constructionStates[field] = state;
                    SetFlag(state, destroying, _functionSource);
                }
                continue;
            }
            var guard = new TemporaryGuard(DestructorPlace(receiver.Project(new MirFieldProjection(field)), field.Type),
                Temporary(BuiltinTypes.Bool, _functionSource), destructor);
            _constructionFields.Add(field, guard);
            _constructionStates.Add(field, guard.Active);
            _lifecycleFields.Add(guard);
            SetFlag(guard.Active, destroying, _functionSource);
        }
        _unwindTarget = _rethrowTarget = CleanupChain(_lifecycleFields, _unwind, _unwind, true, _functionSource);
    }

    private void LifecycleCompleted(FunctionSymbol completed)
    {
        if (_bound.Symbol.FunctionKind != FunctionKind.Constructor ||
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
        else if (_constructionBase is { } baseGuard && TypeIdentity.AreSame(completed.ContainingType, owner.BaseType))
            SetFlag(baseGuard.Active, true, _functionSource);
    }

    private void DestroyFields(MirSourceInfo source)
    {
        Block after = NewBlock();
        Jump(CleanupChain(_lifecycleFields, after, _unwind, _exceptionalFinalizerDepth > 0, source));
        _current = after;
    }
}
