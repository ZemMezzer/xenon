using System.Collections.Immutable;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Syntax;

namespace Xenon.Compiler.Libraries;

public static class XelibBodyCodec
{
    public static void Collect(BoundNode node, Action<TypeSymbol> addType, Action<Symbol> addSymbol)
    {
        if (node is BoundExpression expression) addType(expression.Type);
        switch (node)
        {
            case BoundVariableDeclarationStatement value:
                addType(value.Variable.Type);
                if (value.Variable.Destructor is { } localDestructor) addSymbol(localDestructor);
                break;
            case BoundVariableExpression value when value.Variable is not LocalVariableSymbol:
                addSymbol(value.Variable);
                break;
            case BoundThisExpression value: addType(value.ContainingType); break;
            case BoundMoveExpression value:
                if (value.TrackedVariable is not null && value.TrackedVariable is not LocalVariableSymbol)
                    addSymbol(value.TrackedVariable);
                foreach (FieldSymbol trackedField in value.TrackedPath) addSymbol(trackedField);
                break;
            case BoundDestroyFieldsExpression value: addType(value.StructType); break;
            case BoundOwnershipDestructionExpression value:
                addType(value.OwnershipType);
                if (value.ElementDestructor is { } ownershipDestructor) addSymbol(ownershipDestructor);
                break;
            case BoundStorageDestructionExpression value:
                addType(value.StorageType);
                if (value.ElementDestructor is { } storageDestructor) addSymbol(storageDestructor);
                break;
            case BoundUniqueAdoptionExpression value: addType(value.UniqueType); break;
            case BoundSharedAdoptionExpression value: addType(value.SharedType); break;
            case BoundWeakConversionExpression value: addType(value.WeakType); break;
            case BoundLockExpression value: addType(value.SharedType); break;
            case BoundFullExpression value:
                foreach (BoundFullExpressionTemporary temporary in value.Temporaries)
                    addSymbol(temporary.Destructor);
                break;
            case BoundAssignmentExpression value when value.ConstructorField is { } constructorField:
                addSymbol(constructorField); break;
            case BoundCompoundAccessorAssignmentExpression value:
                addSymbol(value.Getter); addSymbol(value.Setter);
                if (value.InterfaceType is { } type) addType(type);
                break;
            case BoundMethodCallExpression value: addSymbol(value.Method); break;
            case BoundDeferredGenericMethodCallExpression value: addSymbol(value.Requirement); break;
            case BoundDeferredGenericOperationExpression value:
                addSymbol(value.Requirement);
                if (!value.TypeArguments.IsDefault)
                    foreach (TypeSymbol argument in value.TypeArguments) addType(argument);
                break;
            case BoundPropertySetExpression value: addSymbol(value.Property); break;
            case BoundInterfacePropertySetExpression value:
                addType(value.InterfaceType); addSymbol(value.Property); break;
            case BoundIndexerSetExpression value: addSymbol(value.Indexer); break;
            case BoundInterfaceIndexerSetExpression value:
                addType(value.InterfaceType); addSymbol(value.Indexer); break;
            case BoundMemberAccessExpression value: addSymbol(value.Field); break;
            case BoundStaticFieldExpression value: addSymbol(value.Field); break;
            case BoundTypeLayoutExpression value:
                addType(value.TargetType); if (value.Field is { } layoutField) addSymbol(layoutField); break;
            case BoundInterfaceConversionExpression value:
                addType(value.SourceType); addType(value.InterfaceType); break;
            case BoundReferenceConversionExpression value: addType(value.ReferenceType); break;
            case BoundReferenceDereferenceExpression value: addType(value.ReferenceType); break;
            case BoundLifetimeValueExpression value: addType(value.ModifierType); break;
            case BoundStorageConstructExpression value:
                addType(value.ValueType); if (value.Constructor is { } storageConstructor) addSymbol(storageConstructor); break;
            case BoundExplicitDestructExpression value:
                addType(value.ValueType);
                if (value.Destructor is { } explicitDestructor) addSymbol(explicitDestructor);
                if (value.TrackedVariable is not null && value.TrackedVariable is not LocalVariableSymbol)
                    addSymbol(value.TrackedVariable);
                foreach (FieldSymbol trackedField in value.TrackedPath) addSymbol(trackedField);
                break;
            case BoundStorageMoveExpression value: addType(value.StorageType); break;
            case BoundInterfaceMethodCallExpression value:
                addType(value.InterfaceType); addSymbol(value.Method); break;
            case BoundIndexExpression value: addType(value.ElementType); break;
            case BoundStructConstructionExpression value: addType(value.StructType); break;
            case BoundConstructorCallExpression value:
                addType(value.StructType); addSymbol(value.Constructor); break;
            case BoundBaseLifecycleCallExpression value: addSymbol(value.Function); break;
            case BoundArrayCreationExpression value:
                addType(value.ElementType); addType(value.ArrayType); break;
            case BoundNewExpression value:
                addType(value.AllocatedType); addType(value.PointerType);
                if (value.Constructor is { } allocationConstructor) addSymbol(allocationConstructor);
                break;
            case BoundFreeExpression value when value.Destructor is { } freeDestructor: addSymbol(freeDestructor); break;
            case BoundCallExpression value: addSymbol(value.Function); break;
            case BoundFunctionAddressExpression value:
                addSymbol(value.Function); addType(value.FunctionPointerType); break;
            case BoundIndirectCallExpression value: addType(value.FunctionPointerType); break;
        }
        foreach (BoundNode child in Children(node)) Collect(child, addType, addSymbol);
    }

    public static XelibBodyNode Encode(BoundBlockStatement root, FunctionSymbol function,
        Func<TypeSymbol, int> typeId, Func<Symbol, XelibSymbolReference> symbolReference)
    {
        var locals = new Dictionary<LocalVariableSymbol, int>(ReferenceEqualityComparer.Instance);
        CollectLocals(root, locals);
        XelibBodyNode encoded = EncodeNode(root, locals, typeId, symbolReference);
        return encoded with
        {
            Locals = locals.OrderBy(pair => pair.Value).Select(pair => new XelibLocalRecord(
                pair.Value, pair.Key.Name, typeId(pair.Key.Type), pair.Key.IsReadonly,
                (ushort)pair.Key.ArrayStorage, pair.Key.RequiresArrayCleanupTransfer,
                pair.Key.Destructor is null ? null : symbolReference(pair.Key.Destructor))).ToImmutableArray(),
        };
    }

    public static XelibBodyNode EncodeExpression(BoundExpression expression,
        Func<TypeSymbol, int> typeId, Func<Symbol, XelibSymbolReference> symbolReference) =>
        EncodeNode(expression,
            new Dictionary<LocalVariableSymbol, int>(ReferenceEqualityComparer.Instance),
            typeId, symbolReference);

    public static BoundBlockStatement Decode(XelibBodyNode root, FunctionSymbol function,
        Func<int, TypeSymbol> type, Func<XelibSymbolReference, Symbol> symbol,
        Func<BoundExpression, Symbol, ImmutableArray<BoundExpression>, bool, BoundExpression>?
            deferredGenericMethod = null,
        Func<BoundDeferredGenericOperationKind, BoundExpression?, Symbol,
            ImmutableArray<BoundExpression>, BoundExpression?, SyntaxKind, bool, TypeSymbol,
            ImmutableArray<TypeSymbol>, BoundExpression>? deferredGenericOperation = null)
    {
        var locals = new Dictionary<int, LocalVariableSymbol>();
        foreach (XelibLocalRecord record in root.Locals)
        {
            if (record.Id <= 0 || locals.ContainsKey(record.Id)) Invalid("duplicate or invalid local ID");
            var local = new LocalVariableSymbol(record.Name, type(record.TypeId), function, record.IsReadonly)
            {
                ArrayStorage = (ArrayStorageKind)record.ArrayStorage,
                RequiresArrayCleanupTransfer = record.RequiresArrayCleanupTransfer,
                Destructor = record.Destructor is null ? null : (FunctionSymbol)symbol(record.Destructor),
            };
            locals.Add(record.Id, local);
        }
        return DecodeNode(root, locals, type, symbol, deferredGenericMethod,
            deferredGenericOperation) as BoundBlockStatement ??
            throw Invalid("function root is not a block");
    }

    public static BoundExpression DecodeExpression(XelibBodyNode root,
        Func<int, TypeSymbol> type, Func<XelibSymbolReference, Symbol> symbol) =>
        DecodeNode(root, new Dictionary<int, LocalVariableSymbol>(), type, symbol, null, null)
            as BoundExpression ?? throw Invalid("generic constant payload is not an expression");

    private static void CollectLocals(BoundNode node, Dictionary<LocalVariableSymbol, int> locals)
    {
        if (node is BoundVariableDeclarationStatement declaration && !locals.ContainsKey(declaration.Variable))
            locals.Add(declaration.Variable, locals.Count + 1);
        if (node is BoundVariableExpression { Variable: LocalVariableSymbol local } && !locals.ContainsKey(local))
            locals.Add(local, locals.Count + 1);
        if (node is BoundMoveExpression { TrackedVariable: LocalVariableSymbol moved } && !locals.ContainsKey(moved))
            locals.Add(moved, locals.Count + 1);
        if (node is BoundExplicitDestructExpression { TrackedVariable: LocalVariableSymbol destructed } &&
            !locals.ContainsKey(destructed)) locals.Add(destructed, locals.Count + 1);
        foreach (BoundNode child in Children(node)) CollectLocals(child, locals);
    }

    private static XelibBodyNode EncodeNode(BoundNode node,
        IReadOnlyDictionary<LocalVariableSymbol, int> locals, Func<TypeSymbol, int> typeId,
        Func<Symbol, XelibSymbolReference> symbol)
    {
        XelibBodyNode E(BoundNode value) => EncodeNode(value, locals, typeId, symbol);
        ImmutableArray<XelibBodyNode> EAll(IEnumerable<BoundNode> values) => values.Select(E).ToImmutableArray();
        (int Local, XelibSymbolReference? Symbol) Variable(VariableSymbol? value) => value switch
        {
            null => (0, null),
            LocalVariableSymbol local when locals.TryGetValue(local, out int id) => (id, null),
            _ => (0, symbol(value)),
        };
        switch (node)
        {
            case BoundBlockStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Block,
                    Flag1 = value.ExitCleanup is not null, Flag2 = value.RetainsStackStorage,
                    Children = EAll(value.Statements.Cast<BoundNode>().Concat(
                        value.ExitCleanup is null ? [] : [value.ExitCleanup])) };
            case BoundVariableDeclarationStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.VariableDeclaration,
                    LocalId = locals[value.Variable], Flag1 = value.Initializer is not null,
                    Children = value.Initializer is null ? [] : [E(value.Initializer)] };
            case BoundReturnStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Return, Flag1 = value.Expression is not null,
                    Children = value.Expression is null ? [] : [E(value.Expression)] };
            case BoundExpressionStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ExpressionStatement, Children = [E(value.Expression)] };
            case BoundIfStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.If, Flag1 = value.ElseStatement is not null,
                    Children = value.ElseStatement is null ? [E(value.Condition), E(value.ThenStatement)] :
                        [E(value.Condition), E(value.ThenStatement), E(value.ElseStatement)] };
            case BoundWhileStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.While,
                    Children = [E(value.Condition), E(value.Body)] };
            case BoundForStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.For,
                    Flag1 = value.Initializer is not null, Flag2 = value.Condition is not null,
                    Flag3 = value.Increment is not null,
                    Children = EAll(new BoundNode?[] { value.Initializer, value.Condition, value.Increment, value.Body }
                        .OfType<BoundNode>()) };
            case BoundSwitchStatement value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Switch,
                    Children = [E(value.Expression), .. value.Sections.Select(section =>
                        new XelibBodyNode { Opcode = XelibBodyOpcode.SwitchSection, Flag1 = section.Value is not null,
                            Children = section.Value is null ? [E(section.Body)] : [E(section.Value), E(section.Body)] })] };
            case BoundBreakStatement: return new XelibBodyNode { Opcode = XelibBodyOpcode.Break };
            case BoundContinueStatement: return new XelibBodyNode { Opcode = XelibBodyOpcode.Continue };
            case BoundLiteralExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Literal, TypeId = typeId(value.Type),
                    Constant = value.Value is null ? new XelibConstantValue(XelibConstantKind.Null, null) :
                        XelibIrBuilder.Constant(value.Value) };
            case BoundVariableExpression value:
            {
                var reference = Variable(value.Variable);
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Variable, TypeId = typeId(value.Type),
                    LocalId = reference.Local, Symbol = reference.Symbol };
            }
            case BoundThisExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.This, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.ContainingType) };
            case BoundUnaryExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Unary, TypeId = typeId(value.Type),
                    Operator = Map(value.OperatorKind), Flag1 = value.IsPostfix, Children = [E(value.Operand)] };
            case BoundMoveExpression value:
            {
                var tracked = Variable(value.TrackedVariable);
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Move, TypeId = typeId(value.Type),
                    LocalId = tracked.Local, Symbol = tracked.Symbol,
                    Symbols = value.TrackedPath.Select(item => symbol(item)).ToImmutableArray(), Children = [E(value.Source)] };
            }
            case BoundCopyExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Copy, TypeId = typeId(value.Type), Children = [E(value.Source)] };
            case BoundDestroyFieldsExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.DestroyFields, TypeId = typeId(value.StructType) };
            case BoundOwnershipDestructionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.OwnershipDestruction,
                    TypeId = typeId(value.OwnershipType), Symbol = value.ElementDestructor is null ? null : symbol(value.ElementDestructor) };
            case BoundStorageDestructionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.StorageDestruction,
                    TypeId = typeId(value.StorageType), Symbol = value.ElementDestructor is null ? null : symbol(value.ElementDestructor) };
            case BoundUniqueAdoptionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.UniqueAdoption, TypeId = typeId(value.UniqueType),
                    Children = [E(value.Allocation)] };
            case BoundSharedAdoptionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.SharedAdoption, TypeId = typeId(value.SharedType),
                    Children = [E(value.Allocation)] };
            case BoundWeakConversionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.WeakConversion, TypeId = typeId(value.WeakType),
                    Children = [E(value.Shared)] };
            case BoundLockExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Lock, TypeId = typeId(value.SharedType),
                    Children = [E(value.Weak)] };
            case BoundFullExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.FullExpression, TypeId = typeId(value.Type),
                    Children = [E(value.Expression)], Temporaries = value.Temporaries.Select(item =>
                        new XelibTemporaryRecord(E(item.Value), symbol(item.Destructor))).ToImmutableArray() };
            case BoundBinaryExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Binary, TypeId = typeId(value.Type),
                    Operator = Map(value.OperatorKind), Children = [E(value.Left), E(value.Right)] };
            case BoundAssignmentExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Assignment, TypeId = typeId(value.Type),
                    Operator = Map(value.OperatorKind), Flag1 = value.IsInitialization,
                    Integer = (int)value.MovedPlaceReinitialization,
                    Symbol = value.ConstructorField is null ? null : symbol(value.ConstructorField),
                    Flag2 = value.RequiresRuntimeInitializationCheck,
                    Children = [E(value.Target), E(value.Expression)] };
            case BoundCompareExchangeExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.CompareExchange, TypeId = typeId(value.Type),
                    Children = [E(value.Target), E(value.Expected), E(value.Desired)] };
            case BoundSwapExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Swap, TypeId = typeId(value.Type),
                    Children = [E(value.Left), E(value.Right)] };
            case BoundCompoundAccessorAssignmentExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.CompoundAccessorAssignment,
                    TypeId = typeId(value.Type), AuxTypeId = value.InterfaceType is null ? 0 : typeId(value.InterfaceType),
                    Symbol = symbol(value.Getter), Symbol2 = symbol(value.Setter), Operator = Map(value.OperatorKind),
                    Flag1 = value.IsPointerAccess,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E), E(value.Value)],
                    Integer = value.Arguments.Length };
            case BoundMethodCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.MethodCall, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Method), Flag1 = value.IsPointerAccess,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E)] };
            case BoundDeferredGenericMethodCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.DeferredGenericMethodCall,
                    TypeId = typeId(value.Type), Symbol = symbol(value.Requirement),
                    Flag1 = value.IsPointerAccess,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E)] };
            case BoundDeferredGenericOperationExpression value:
                return new XelibBodyNode
                {
                    Opcode = Map(value.Operation),
                    TypeId = typeId(value.Type),
                    Symbol = symbol(value.Requirement),
                    Operator = Map(value.OperatorKind),
                    Flag1 = value.IsPointerAccess,
                    Flag2 = value.Receiver is not null,
                    Flag3 = value.Value is not null,
                    Integer = value.Arguments.Length,
                    Integers = value.TypeArguments.IsDefault
                        ? [] : value.TypeArguments.Select(typeId).ToImmutableArray(),
                    Children = EAll(Optional(value.Receiver).Concat(value.Arguments)
                        .Concat(Optional(value.Value))),
                };
            case BoundPropertySetExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.PropertySet, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Property), Flag1 = value.IsPointerAccess,
                    Children = [E(value.Receiver), E(value.Value)] };
            case BoundInterfacePropertySetExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.InterfacePropertySet, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.InterfaceType), Symbol = symbol(value.Property),
                    Flag1 = value.IsPointerAccess, Children = [E(value.Receiver), E(value.Value)] };
            case BoundIndexerSetExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.IndexerSet, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Indexer), Integer = value.Arguments.Length,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E), E(value.Value)] };
            case BoundInterfaceIndexerSetExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.InterfaceIndexerSet, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.InterfaceType), Symbol = symbol(value.Indexer), Integer = value.Arguments.Length,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E), E(value.Value)] };
            case BoundMemberAccessExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.MemberAccess, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Field), Flag1 = value.IsPointerAccess, Children = [E(value.Receiver)] };
            case BoundStaticFieldExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.StaticField, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Field) };
            case BoundTypeLayoutExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.TypeLayout, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.TargetType), Operator = Map(value.OperatorKind),
                    Symbol = value.Field is null ? null : symbol(value.Field) };
            case BoundCastExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Cast, TypeId = typeId(value.TargetType),
                    Children = [E(value.Expression)] };
            case BoundInterfaceConversionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.InterfaceConversion,
                    TypeId = typeId(value.InterfaceType), AuxTypeId = typeId(value.SourceType), Children = [E(value.Source)] };
            case BoundReferenceConversionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ReferenceConversion,
                    TypeId = typeId(value.ReferenceType), Children = [E(value.Source)] };
            case BoundReferenceDereferenceExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ReferenceDereference,
                    TypeId = typeId(value.Type), AuxTypeId = typeId(value.ReferenceType), Children = [E(value.Reference)] };
            case BoundLifetimeValueExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.LifetimeValue,
                    TypeId = typeId(value.Type), AuxTypeId = typeId(value.ModifierType), Children = [E(value.Source)] };
            case BoundDefaultValueExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.DefaultValue, TypeId = typeId(value.ValueType) };
            case BoundStorageConstructExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.StorageConstruct, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.ValueType), Symbol = value.Constructor is null ? null : symbol(value.Constructor),
                    Flag1 = value.Value is not null, Flag2 = value.IsDefaultInitialization,
                    Integer = value.Arguments.Length,
                    Children = new[] { E(value.Storage) }
                        .Concat(value.Value is null ? [] : [E(value.Value)])
                        .Concat(value.Arguments.Select(item => E(item))).ToImmutableArray() };
            case BoundExplicitDestructExpression value:
            {
                var tracked = Variable(value.TrackedVariable);
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ExplicitDestruct, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.ValueType), Symbol = value.Destructor is null ? null : symbol(value.Destructor),
                    Symbol2 = tracked.Symbol, LocalId = tracked.Local,
                    Symbols = value.TrackedPath.Select(item => symbol(item)).ToImmutableArray(), Children = [E(value.Target)] };
            }
            case BoundStorageMoveExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.StorageMove, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.StorageType), Children = [E(value.Storage)] };
            case BoundInterfaceMethodCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.InterfaceMethodCall, TypeId = typeId(value.Type),
                    AuxTypeId = typeId(value.InterfaceType), Symbol = symbol(value.Method), Flag1 = value.IsPointerAccess,
                    Children = [E(value.Receiver), .. value.Arguments.Select(E)] };
            case BoundIndexExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Index, TypeId = typeId(value.ElementType),
                    Children = [E(value.Receiver), .. value.Indices.Select(E)] };
            case BoundStructConstructionExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.StructConstruction, TypeId = typeId(value.StructType),
                    Flag1 = value.IsDefaultInitialization, Children = value.Arguments.Select(E).ToImmutableArray() };
            case BoundConstructorCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ConstructorCall, TypeId = typeId(value.StructType),
                    Symbol = symbol(value.Constructor), Children = value.Arguments.Select(E).ToImmutableArray() };
            case BoundBaseLifecycleCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.BaseLifecycleCall, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Function), Children = value.Arguments.Select(E).ToImmutableArray() };
            case BoundArrayCreationExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ArrayCreation, TypeId = typeId(value.ArrayType),
                    AuxTypeId = typeId(value.ElementType), Integer = (int)value.Storage,
                    Children = value.Dimensions.Select(E).ToImmutableArray() };
            case BoundArrayMetadataExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.ArrayMetadata, TypeId = typeId(value.Type),
                    Text = value.Member, Flag1 = value.Dimension is not null,
                    Children = value.Dimension is null ? [E(value.Receiver)] : [E(value.Receiver), E(value.Dimension)] };
            case BoundNewExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.New, TypeId = typeId(value.PointerType),
                    AuxTypeId = typeId(value.AllocatedType), Symbol = value.Constructor is null ? null : symbol(value.Constructor),
                    Flag1 = value.IsPositionalInitialization, Flag2 = value.IsDefaultInitialization,
                    Children = value.Arguments.Select(E).ToImmutableArray() };
            case BoundFreeExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Free, TypeId = typeId(value.Type),
                    Symbol = value.Destructor is null ? null : symbol(value.Destructor), Children = [E(value.Pointer)] };
            case BoundCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.Call, TypeId = typeId(value.Type),
                    Symbol = symbol(value.Function), Children = value.Arguments.Select(E).ToImmutableArray() };
            case BoundFunctionAddressExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.FunctionAddress,
                    TypeId = typeId(value.FunctionPointerType), Symbol = symbol(value.Function) };
            case BoundIndirectCallExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.IndirectCall,
                    TypeId = typeId(value.Type), AuxTypeId = typeId(value.FunctionPointerType),
                    Children = [E(value.Target), .. value.Arguments.Select(E)] };
            case BoundDeferredConstantExpression value:
                return new XelibBodyNode { Opcode = XelibBodyOpcode.DeferredConstant, TypeId = typeId(value.Type) };
            case BoundErrorExpression:
                throw Invalid("an error expression cannot be emitted");
            default:
                throw Invalid($"unsupported bound node '{node.GetType().Name}'");
        }
    }

    private static BoundNode DecodeNode(XelibBodyNode node,
        IReadOnlyDictionary<int, LocalVariableSymbol> locals, Func<int, TypeSymbol> type,
        Func<XelibSymbolReference, Symbol> symbol,
        Func<BoundExpression, Symbol, ImmutableArray<BoundExpression>, bool, BoundExpression>?
            deferredGenericMethod,
        Func<BoundDeferredGenericOperationKind, BoundExpression?, Symbol,
            ImmutableArray<BoundExpression>, BoundExpression?, SyntaxKind, bool, TypeSymbol,
            ImmutableArray<TypeSymbol>, BoundExpression>? deferredGenericOperation)
    {
        BoundNode D(int index) => DecodeNode(node.Children[index], locals, type, symbol,
            deferredGenericMethod, deferredGenericOperation);
        BoundExpression E(int index) => D(index) as BoundExpression ?? throw Invalid("expected expression child");
        BoundStatement S(int index) => D(index) as BoundStatement ?? throw Invalid("expected statement child");
        FunctionSymbol F(XelibSymbolReference? reference) => (FunctionSymbol)symbol(Required(reference));
        T Sym<T>(XelibSymbolReference? reference) where T : Symbol => (T)symbol(Required(reference));
        VariableSymbol Variable(int localId, XelibSymbolReference? reference) => localId != 0
            ? locals.GetValueOrDefault(localId) ?? throw Invalid($"unknown local ID {localId}")
            : (VariableSymbol)symbol(Required(reference));
        EnsureChildren(node);
        switch (node.Opcode)
        {
            case XelibBodyOpcode.Block:
            {
                int statementCount = node.Children.Length - (node.Flag1 ? 1 : 0);
                var result = new BoundBlockStatement(Enumerable.Range(0, statementCount).Select(S).ToImmutableArray())
                {
                    ExitCleanup = node.Flag1 ? E(statementCount) : null,
                    RetainsStackStorage = node.Flag2,
                };
                return result;
            }
            case XelibBodyOpcode.VariableDeclaration:
                return new BoundVariableDeclarationStatement(
                    locals.GetValueOrDefault(node.LocalId) ?? throw Invalid($"unknown local ID {node.LocalId}"),
                    node.Flag1 ? E(0) : null);
            case XelibBodyOpcode.Return: return new BoundReturnStatement(node.Flag1 ? E(0) : null);
            case XelibBodyOpcode.ExpressionStatement: return new BoundExpressionStatement(E(0));
            case XelibBodyOpcode.If: return new BoundIfStatement(E(0), S(1), node.Flag1 ? S(2) : null);
            case XelibBodyOpcode.While: return new BoundWhileStatement(E(0), S(1));
            case XelibBodyOpcode.For:
            {
                int index = 0;
                BoundStatement? initializer = node.Flag1 ? S(index++) : null;
                BoundExpression? condition = node.Flag2 ? E(index++) : null;
                BoundExpression? increment = node.Flag3 ? E(index++) : null;
                return new BoundForStatement(initializer, condition, increment, S(index));
            }
            case XelibBodyOpcode.Switch:
                return new BoundSwitchStatement(E(0), node.Children.Skip(1).Select(section =>
                {
                    if (section.Opcode != XelibBodyOpcode.SwitchSection) throw Invalid("expected switch section");
                    BoundExpression? value = section.Flag1
                        ? DecodeNode(section.Children[0], locals, type, symbol,
                            deferredGenericMethod, deferredGenericOperation) as BoundExpression
                        : null;
                    int bodyIndex = section.Flag1 ? 1 : 0;
                    BoundBlockStatement body = DecodeNode(section.Children[bodyIndex], locals, type, symbol,
                        deferredGenericMethod, deferredGenericOperation)
                        as BoundBlockStatement ?? throw Invalid("switch section body is not a block");
                    return new BoundSwitchSection(value, body);
                }).ToImmutableArray());
            case XelibBodyOpcode.Break: return new BoundBreakStatement();
            case XelibBodyOpcode.Continue: return new BoundContinueStatement();
            case XelibBodyOpcode.Literal: return new BoundLiteralExpression(
                ParseConstant(node.Constant, type(node.TypeId)), type(node.TypeId));
            case XelibBodyOpcode.Variable: return new BoundVariableExpression(Variable(node.LocalId, node.Symbol));
            case XelibBodyOpcode.This: return new BoundThisExpression(
                (DeclaredTypeSymbol)type(node.AuxTypeId), (PointerTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.Unary: return new BoundUnaryExpression(Map(node.Operator), E(0),
                type(node.TypeId), node.Flag1);
            case XelibBodyOpcode.Move:
                return new BoundMoveExpression(E(0)) { TrackedVariable = node.LocalId != 0 || node.Symbol is not null
                    ? Variable(node.LocalId, node.Symbol) : null,
                    TrackedPath = node.Symbols.Select(item => (FieldSymbol)symbol(item)).ToImmutableArray() };
            case XelibBodyOpcode.Copy: return new BoundCopyExpression(E(0));
            case XelibBodyOpcode.DestroyFields: return new BoundDestroyFieldsExpression((StructTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.OwnershipDestruction: return new BoundOwnershipDestructionExpression(
                (OwnershipTypeSymbol)type(node.TypeId), node.Symbol is null ? null : F(node.Symbol));
            case XelibBodyOpcode.StorageDestruction: return new BoundStorageDestructionExpression(
                (StorageTypeSymbol)type(node.TypeId), node.Symbol is null ? null : F(node.Symbol));
            case XelibBodyOpcode.UniqueAdoption: return new BoundUniqueAdoptionExpression(E(0), (UniqueTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.SharedAdoption: return new BoundSharedAdoptionExpression(E(0), (SharedTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.WeakConversion: return new BoundWeakConversionExpression(E(0), (WeakTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.Lock: return new BoundLockExpression(E(0), (SharedTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.FullExpression: return new BoundFullExpression(E(0), node.Temporaries.Select(item =>
                new BoundFullExpressionTemporary((BoundExpression)DecodeNode(item.Value, locals, type, symbol,
                        deferredGenericMethod, deferredGenericOperation),
                    (FunctionSymbol)symbol(item.Destructor))).ToImmutableArray());
            case XelibBodyOpcode.Binary: return new BoundBinaryExpression(E(0), Map(node.Operator), E(1), type(node.TypeId));
            case XelibBodyOpcode.Assignment: return new BoundAssignmentExpression(E(0), Map(node.Operator), E(1))
            {
                IsInitialization = node.Flag1,
                MovedPlaceReinitialization = (MovedPlaceReinitializationState)node.Integer,
                ConstructorField = node.Symbol is null ? null : Sym<FieldSymbol>(node.Symbol),
                RequiresRuntimeInitializationCheck = node.Flag2,
            };
            case XelibBodyOpcode.CompareExchange: return new BoundCompareExchangeExpression(E(0), E(1), E(2));
            case XelibBodyOpcode.Swap: return new BoundSwapExpression(E(0), E(1));
            case XelibBodyOpcode.CompoundAccessorAssignment:
                return new BoundCompoundAccessorAssignmentExpression(E(0), F(node.Symbol), F(node.Symbol2),
                    node.Children.Skip(1).Take(node.Integer).Select((_, i) => E(i + 1)).ToImmutableArray(),
                    Map(node.Operator), E(node.Integer + 1), node.Flag1,
                    node.AuxTypeId == 0 ? null : (InterfaceTypeSymbol)type(node.AuxTypeId));
            case XelibBodyOpcode.MethodCall: return new BoundMethodCallExpression(E(0), F(node.Symbol),
                node.Children.Skip(1).Select((_, i) => E(i + 1)).ToImmutableArray(), node.Flag1);
            case XelibBodyOpcode.DeferredGenericMethodCall:
            {
                if (deferredGenericMethod is null)
                    throw Invalid("deferred generic method call appears outside a generic implementation");
                return deferredGenericMethod(E(0), symbol(Required(node.Symbol)),
                    node.Children.Skip(1).Select((_, i) => E(i + 1)).ToImmutableArray(), node.Flag1);
            }
            case XelibBodyOpcode.DeferredGenericFunctionCall or
                 XelibBodyOpcode.DeferredGenericFieldGet or
                 XelibBodyOpcode.DeferredGenericFieldSet or
                 XelibBodyOpcode.DeferredGenericPropertyGet or
                 XelibBodyOpcode.DeferredGenericPropertySet or
                 XelibBodyOpcode.DeferredGenericIndexerGet or
                 XelibBodyOpcode.DeferredGenericIndexerSet or
                 XelibBodyOpcode.DeferredGenericConstruction or
                 XelibBodyOpcode.DeferredGenericAllocation:
            {
                if (deferredGenericOperation is null)
                    throw Invalid("deferred generic operation appears outside a generic implementation");
                int index = 0;
                BoundExpression? receiver = node.Flag2 ? E(index++) : null;
                if (node.Integer < 0 || index + node.Integer + (node.Flag3 ? 1 : 0) != node.Children.Length)
                    throw Invalid("deferred generic operation has an invalid child count");
                ImmutableArray<BoundExpression> arguments = Enumerable.Range(index, node.Integer)
                    .Select(E).ToImmutableArray();
                index += node.Integer;
                BoundExpression? value = node.Flag3 ? E(index) : null;
                return deferredGenericOperation(MapDeferred(node.Opcode), receiver,
                    symbol(Required(node.Symbol)), arguments, value, Map(node.Operator), node.Flag1,
                    type(node.TypeId), node.Integers.Select(type).ToImmutableArray());
            }
            case XelibBodyOpcode.PropertySet: return new BoundPropertySetExpression(E(0),
                Sym<PropertySymbol>(node.Symbol), E(1), node.Flag1);
            case XelibBodyOpcode.InterfacePropertySet: return new BoundInterfacePropertySetExpression(E(0),
                (InterfaceTypeSymbol)type(node.AuxTypeId), Sym<InterfacePropertySymbol>(node.Symbol), E(1), node.Flag1);
            case XelibBodyOpcode.IndexerSet: return new BoundIndexerSetExpression(E(0), Sym<IndexerSymbol>(node.Symbol),
                node.Children.Skip(1).Take(node.Integer).Select((_, i) => E(i + 1)).ToImmutableArray(), E(node.Integer + 1));
            case XelibBodyOpcode.InterfaceIndexerSet: return new BoundInterfaceIndexerSetExpression(E(0),
                (InterfaceTypeSymbol)type(node.AuxTypeId), Sym<InterfaceIndexerSymbol>(node.Symbol),
                node.Children.Skip(1).Take(node.Integer).Select((_, i) => E(i + 1)).ToImmutableArray(), E(node.Integer + 1));
            case XelibBodyOpcode.MemberAccess: return new BoundMemberAccessExpression(E(0),
                Sym<FieldSymbol>(node.Symbol), node.Flag1);
            case XelibBodyOpcode.StaticField: return new BoundStaticFieldExpression(Sym<FieldSymbol>(node.Symbol));
            case XelibBodyOpcode.TypeLayout: return new BoundTypeLayoutExpression(Map(node.Operator),
                type(node.AuxTypeId), node.Symbol is null ? null : Sym<FieldSymbol>(node.Symbol));
            case XelibBodyOpcode.Cast: return new BoundCastExpression(E(0), type(node.TypeId));
            case XelibBodyOpcode.InterfaceConversion: return new BoundInterfaceConversionExpression(E(0),
                (StructTypeSymbol)type(node.AuxTypeId), (InterfaceTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.ReferenceConversion: return new BoundReferenceConversionExpression(E(0),
                (ReferenceTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.ReferenceDereference: return new BoundReferenceDereferenceExpression(E(0),
                (ReferenceTypeSymbol)type(node.AuxTypeId));
            case XelibBodyOpcode.LifetimeValue: return new BoundLifetimeValueExpression(E(0),
                (LifetimeModifierTypeSymbol)type(node.AuxTypeId));
            case XelibBodyOpcode.DefaultValue: return new BoundDefaultValueExpression(type(node.TypeId));
            case XelibBodyOpcode.StorageConstruct:
            {
                int index = 1;
                BoundExpression? value = node.Flag1 ? E(index++) : null;
                return new BoundStorageConstructExpression(E(0), type(node.AuxTypeId), value,
                    node.Symbol is null ? null : F(node.Symbol),
                    Enumerable.Range(index, node.Integer).Select(E).ToImmutableArray(), node.Flag2);
            }
            case XelibBodyOpcode.ExplicitDestruct: return new BoundExplicitDestructExpression(E(0),
                type(node.AuxTypeId), node.Symbol is null ? null : F(node.Symbol))
            {
                TrackedVariable = node.LocalId != 0 || node.Symbol2 is not null
                    ? Variable(node.LocalId, node.Symbol2) : null,
                TrackedPath = node.Symbols.Select(item => (FieldSymbol)symbol(item)).ToImmutableArray(),
            };
            case XelibBodyOpcode.StorageMove: return new BoundStorageMoveExpression(E(0),
                (StorageTypeSymbol)type(node.AuxTypeId));
            case XelibBodyOpcode.InterfaceMethodCall: return new BoundInterfaceMethodCallExpression(E(0),
                (InterfaceTypeSymbol)type(node.AuxTypeId), F(node.Symbol),
                node.Children.Skip(1).Select((_, i) => E(i + 1)).ToImmutableArray(), node.Flag1);
            case XelibBodyOpcode.Index:
            {
                ImmutableArray<BoundExpression> indices = node.Children.Skip(1)
                    .Select((_, i) => E(i + 1)).ToImmutableArray();
                if (indices.IsEmpty) throw Invalid("index expression has no indices");
                return new BoundIndexExpression(E(0), indices[0], type(node.TypeId)) { Indices = indices };
            }
            case XelibBodyOpcode.StructConstruction: return new BoundStructConstructionExpression(
                (StructTypeSymbol)type(node.TypeId), node.Children.Select((_, i) => E(i)).ToImmutableArray())
                { IsDefaultInitialization = node.Flag1 };
            case XelibBodyOpcode.ConstructorCall: return new BoundConstructorCallExpression(
                (StructTypeSymbol)type(node.TypeId), F(node.Symbol),
                node.Children.Select((_, i) => E(i)).ToImmutableArray());
            case XelibBodyOpcode.BaseLifecycleCall: return new BoundBaseLifecycleCallExpression(F(node.Symbol),
                node.Children.Select((_, i) => E(i)).ToImmutableArray());
            case XelibBodyOpcode.ArrayCreation:
            {
                ImmutableArray<BoundExpression> dimensions = node.Children.Select((_, i) => E(i)).ToImmutableArray();
                if (dimensions.IsEmpty) throw Invalid("array creation has no dimensions");
                return new BoundArrayCreationExpression(type(node.AuxTypeId), dimensions[0],
                    (ArrayTypeSymbol)type(node.TypeId), (ArrayStorageKind)node.Integer) { Dimensions = dimensions };
            }
            case XelibBodyOpcode.ArrayMetadata: return new BoundArrayMetadataExpression(E(0),
                node.Text ?? throw Invalid("array metadata member is missing"), node.Flag1 ? E(1) : null);
            case XelibBodyOpcode.New: return new BoundNewExpression(type(node.AuxTypeId),
                node.Symbol is null ? null : F(node.Symbol), node.Children.Select((_, i) => E(i)).ToImmutableArray(),
                node.Flag1, (PointerTypeSymbol)type(node.TypeId)) { IsDefaultInitialization = node.Flag2 };
            case XelibBodyOpcode.Free: return new BoundFreeExpression(E(0), node.Symbol is null ? null : F(node.Symbol));
            case XelibBodyOpcode.Call: return new BoundCallExpression(F(node.Symbol),
                node.Children.Select((_, i) => E(i)).ToImmutableArray());
            case XelibBodyOpcode.FunctionAddress: return new BoundFunctionAddressExpression(F(node.Symbol),
                (FunctionPointerTypeSymbol)type(node.TypeId));
            case XelibBodyOpcode.IndirectCall: return new BoundIndirectCallExpression(E(0),
                (FunctionPointerTypeSymbol)type(node.AuxTypeId),
                node.Children.Skip(1).Select((_, i) => E(i + 1)).ToImmutableArray());
            case XelibBodyOpcode.DeferredConstant: return new BoundDeferredConstantExpression(type(node.TypeId));
            default: throw Invalid($"unknown body opcode {(ushort)node.Opcode}");
        }
    }

    private static IEnumerable<BoundNode> Children(BoundNode node) => node switch
    {
        BoundBlockStatement value => value.Statements.Cast<BoundNode>().Concat(
            value.ExitCleanup is null ? [] : [value.ExitCleanup]),
        BoundVariableDeclarationStatement value => Optional(value.Initializer),
        BoundReturnStatement value => Optional(value.Expression),
        BoundExpressionStatement value => [value.Expression],
        BoundIfStatement value => new BoundNode?[] { value.Condition, value.ThenStatement, value.ElseStatement }.OfType<BoundNode>(),
        BoundWhileStatement value => [value.Condition, value.Body],
        BoundForStatement value => new BoundNode?[] { value.Initializer, value.Condition, value.Increment, value.Body }.OfType<BoundNode>(),
        BoundSwitchStatement value => new[] { value.Expression }.Concat(value.Sections.SelectMany(section =>
            new BoundNode?[] { section.Value, section.Body }.OfType<BoundNode>())),
        BoundUnaryExpression value => [value.Operand],
        BoundMoveExpression value => [value.Source],
        BoundCopyExpression value => [value.Source],
        BoundUniqueAdoptionExpression value => [value.Allocation],
        BoundSharedAdoptionExpression value => [value.Allocation],
        BoundWeakConversionExpression value => [value.Shared],
        BoundLockExpression value => [value.Weak],
        BoundFullExpression value => new[] { value.Expression }.Concat(value.Temporaries.Select(item => item.Value)),
        BoundBinaryExpression value => [value.Left, value.Right],
        BoundAssignmentExpression value => [value.Target, value.Expression],
        BoundCompareExchangeExpression value => [value.Target, value.Expected, value.Desired],
        BoundSwapExpression value => [value.Left, value.Right],
        BoundCompoundAccessorAssignmentExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundDeferredGenericMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundDeferredGenericOperationExpression value => Optional(value.Receiver)
            .Concat(value.Arguments).Concat(Optional(value.Value)),
        BoundPropertySetExpression value => [value.Receiver, value.Value],
        BoundInterfacePropertySetExpression value => [value.Receiver, value.Value],
        BoundIndexerSetExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundInterfaceIndexerSetExpression value => new[] { value.Receiver }.Concat(value.Arguments).Append(value.Value),
        BoundMemberAccessExpression value => [value.Receiver],
        BoundCastExpression value => [value.Expression],
        BoundInterfaceConversionExpression value => [value.Source],
        BoundReferenceConversionExpression value => [value.Source],
        BoundReferenceDereferenceExpression value => [value.Reference],
        BoundLifetimeValueExpression value => [value.Source],
        BoundStorageConstructExpression value => new[] { value.Storage }.Concat(Optional(value.Value)).Concat(value.Arguments),
        BoundExplicitDestructExpression value => [value.Target],
        BoundStorageMoveExpression value => [value.Storage],
        BoundInterfaceMethodCallExpression value => new[] { value.Receiver }.Concat(value.Arguments),
        BoundIndexExpression value => new[] { value.Receiver }.Concat(value.Indices),
        BoundStructConstructionExpression value => value.Arguments,
        BoundConstructorCallExpression value => value.Arguments,
        BoundBaseLifecycleCallExpression value => value.Arguments,
        BoundArrayCreationExpression value => value.Dimensions,
        BoundArrayMetadataExpression value => new[] { value.Receiver }.Concat(Optional(value.Dimension)),
        BoundNewExpression value => value.Arguments,
        BoundFreeExpression value => [value.Pointer],
        BoundCallExpression value => value.Arguments,
        BoundIndirectCallExpression value => new[] { value.Target }.Concat(value.Arguments),
        _ => [],
    };

    private static IEnumerable<BoundNode> Optional(BoundNode? value) => value is null ? [] : [value];

    private static void EnsureChildren(XelibBodyNode node)
    {
        int minimum = node.Opcode switch
        {
            XelibBodyOpcode.ExpressionStatement or XelibBodyOpcode.While or XelibBodyOpcode.Unary or
            XelibBodyOpcode.Move or XelibBodyOpcode.Copy or XelibBodyOpcode.UniqueAdoption or
            XelibBodyOpcode.SharedAdoption or XelibBodyOpcode.WeakConversion or XelibBodyOpcode.Lock or
            XelibBodyOpcode.FullExpression or XelibBodyOpcode.MemberAccess or XelibBodyOpcode.Cast or
            XelibBodyOpcode.InterfaceConversion or XelibBodyOpcode.ReferenceConversion or
            XelibBodyOpcode.ReferenceDereference or XelibBodyOpcode.LifetimeValue or
            XelibBodyOpcode.ExplicitDestruct or XelibBodyOpcode.StorageMove or XelibBodyOpcode.Free => 1,
            XelibBodyOpcode.If or XelibBodyOpcode.Binary or XelibBodyOpcode.Assignment or
            XelibBodyOpcode.Swap or XelibBodyOpcode.PropertySet or XelibBodyOpcode.InterfacePropertySet => 2,
            XelibBodyOpcode.CompareExchange => 3,
            XelibBodyOpcode.Switch or XelibBodyOpcode.MethodCall or XelibBodyOpcode.DeferredGenericMethodCall or
            XelibBodyOpcode.InterfaceMethodCall or
            XelibBodyOpcode.IndirectCall => 1,
            _ => 0,
        };
        if (node.Children.Length < minimum) throw Invalid($"opcode '{node.Opcode}' has too few children");
    }

    private static XelibSymbolReference Required(XelibSymbolReference? value) => value ??
        throw Invalid("required symbol reference is missing");
    private static XelibFormatException Invalid(string message) =>
        new(XelibErrorCode.InvalidRecord, message);

    private static object? ParseConstant(XelibConstantValue? value, TypeSymbol type)
    {
        if (value is null || value.Kind == XelibConstantKind.Null) return null;
        string text = value.Value ?? throw Invalid("constant text is missing");
        return value.Kind switch
        {
            XelibConstantKind.Boolean => bool.Parse(text),
            XelibConstantKind.SignedInteger when ReferenceEquals(type, BuiltinTypes.Int) => int.Parse(text),
            XelibConstantKind.SignedInteger => long.Parse(text),
            XelibConstantKind.UnsignedInteger when ReferenceEquals(type, BuiltinTypes.UInt) => uint.Parse(text),
            XelibConstantKind.UnsignedInteger => ulong.Parse(text),
            XelibConstantKind.FloatingPoint when ReferenceEquals(type, BuiltinTypes.Float) => float.Parse(text,
                System.Globalization.CultureInfo.InvariantCulture),
            XelibConstantKind.FloatingPoint => double.Parse(text, System.Globalization.CultureInfo.InvariantCulture),
            XelibConstantKind.String => text,
            _ => throw Invalid("invalid constant kind"),
        };
    }

    private static XelibOperator Map(SyntaxKind kind) => kind switch
    {
        SyntaxKind.PlusToken => XelibOperator.Plus, SyntaxKind.MinusToken => XelibOperator.Minus,
        SyntaxKind.StarToken => XelibOperator.Multiply, SyntaxKind.SlashToken => XelibOperator.Divide,
        SyntaxKind.PercentToken => XelibOperator.Remainder, SyntaxKind.EqualsToken => XelibOperator.Assign,
        SyntaxKind.EqualsEqualsToken => XelibOperator.Equal, SyntaxKind.BangToken => XelibOperator.Not,
        SyntaxKind.BangEqualsToken => XelibOperator.NotEqual, SyntaxKind.LessToken => XelibOperator.Less,
        SyntaxKind.LessOrEqualsToken => XelibOperator.LessOrEqual, SyntaxKind.GreaterToken => XelibOperator.Greater,
        SyntaxKind.GreaterOrEqualsToken => XelibOperator.GreaterOrEqual,
        SyntaxKind.AmpersandToken => XelibOperator.BitAnd, SyntaxKind.AmpersandAmpersandToken => XelibOperator.LogicalAnd,
        SyntaxKind.PipeToken => XelibOperator.BitOr, SyntaxKind.PipePipeToken => XelibOperator.LogicalOr,
        SyntaxKind.CaretToken => XelibOperator.Xor, SyntaxKind.TildeToken => XelibOperator.Complement,
        SyntaxKind.LessLessToken => XelibOperator.ShiftLeft, SyntaxKind.GreaterGreaterToken => XelibOperator.ShiftRight,
        SyntaxKind.PlusPlusToken => XelibOperator.Increment, SyntaxKind.MinusMinusToken => XelibOperator.Decrement,
        SyntaxKind.PlusEqualsToken => XelibOperator.AddAssign, SyntaxKind.MinusEqualsToken => XelibOperator.SubtractAssign,
        SyntaxKind.StarEqualsToken => XelibOperator.MultiplyAssign, SyntaxKind.SlashEqualsToken => XelibOperator.DivideAssign,
        SyntaxKind.PercentEqualsToken => XelibOperator.RemainderAssign,
        SyntaxKind.AmpersandEqualsToken => XelibOperator.AndAssign, SyntaxKind.PipeEqualsToken => XelibOperator.OrAssign,
        SyntaxKind.CaretEqualsToken => XelibOperator.XorAssign,
        SyntaxKind.LessLessEqualsToken => XelibOperator.ShiftLeftAssign,
        SyntaxKind.GreaterGreaterEqualsToken => XelibOperator.ShiftRightAssign,
        SyntaxKind.SizeOfKeyword => XelibOperator.SizeOf, SyntaxKind.AlignOfKeyword => XelibOperator.AlignOf,
        SyntaxKind.OffsetOfKeyword => XelibOperator.OffsetOf, SyntaxKind.CastKeyword => XelibOperator.Cast,
        SyntaxKind.BitCastKeyword => XelibOperator.BitCast,
        _ => throw Invalid($"operator '{kind}' cannot be represented"),
    };

    private static SyntaxKind Map(XelibOperator value) => value switch
    {
        XelibOperator.Plus => SyntaxKind.PlusToken, XelibOperator.Minus => SyntaxKind.MinusToken,
        XelibOperator.Multiply => SyntaxKind.StarToken, XelibOperator.Divide => SyntaxKind.SlashToken,
        XelibOperator.Remainder => SyntaxKind.PercentToken, XelibOperator.Assign => SyntaxKind.EqualsToken,
        XelibOperator.Equal => SyntaxKind.EqualsEqualsToken, XelibOperator.Not => SyntaxKind.BangToken,
        XelibOperator.NotEqual => SyntaxKind.BangEqualsToken, XelibOperator.Less => SyntaxKind.LessToken,
        XelibOperator.LessOrEqual => SyntaxKind.LessOrEqualsToken, XelibOperator.Greater => SyntaxKind.GreaterToken,
        XelibOperator.GreaterOrEqual => SyntaxKind.GreaterOrEqualsToken,
        XelibOperator.BitAnd => SyntaxKind.AmpersandToken, XelibOperator.LogicalAnd => SyntaxKind.AmpersandAmpersandToken,
        XelibOperator.BitOr => SyntaxKind.PipeToken, XelibOperator.LogicalOr => SyntaxKind.PipePipeToken,
        XelibOperator.Xor => SyntaxKind.CaretToken, XelibOperator.Complement => SyntaxKind.TildeToken,
        XelibOperator.ShiftLeft => SyntaxKind.LessLessToken, XelibOperator.ShiftRight => SyntaxKind.GreaterGreaterToken,
        XelibOperator.Increment => SyntaxKind.PlusPlusToken, XelibOperator.Decrement => SyntaxKind.MinusMinusToken,
        XelibOperator.AddAssign => SyntaxKind.PlusEqualsToken, XelibOperator.SubtractAssign => SyntaxKind.MinusEqualsToken,
        XelibOperator.MultiplyAssign => SyntaxKind.StarEqualsToken, XelibOperator.DivideAssign => SyntaxKind.SlashEqualsToken,
        XelibOperator.RemainderAssign => SyntaxKind.PercentEqualsToken,
        XelibOperator.AndAssign => SyntaxKind.AmpersandEqualsToken, XelibOperator.OrAssign => SyntaxKind.PipeEqualsToken,
        XelibOperator.XorAssign => SyntaxKind.CaretEqualsToken,
        XelibOperator.ShiftLeftAssign => SyntaxKind.LessLessEqualsToken,
        XelibOperator.ShiftRightAssign => SyntaxKind.GreaterGreaterEqualsToken,
        XelibOperator.SizeOf => SyntaxKind.SizeOfKeyword, XelibOperator.AlignOf => SyntaxKind.AlignOfKeyword,
        XelibOperator.OffsetOf => SyntaxKind.OffsetOfKeyword, XelibOperator.Cast => SyntaxKind.CastKeyword,
        XelibOperator.BitCast => SyntaxKind.BitCastKeyword,
        _ => throw Invalid($"unknown operator {(ushort)value}"),
    };

    private static XelibBodyOpcode Map(BoundDeferredGenericOperationKind value) => value switch
    {
        BoundDeferredGenericOperationKind.FunctionCall => XelibBodyOpcode.DeferredGenericFunctionCall,
        BoundDeferredGenericOperationKind.FieldGet => XelibBodyOpcode.DeferredGenericFieldGet,
        BoundDeferredGenericOperationKind.FieldSet => XelibBodyOpcode.DeferredGenericFieldSet,
        BoundDeferredGenericOperationKind.PropertyGet => XelibBodyOpcode.DeferredGenericPropertyGet,
        BoundDeferredGenericOperationKind.PropertySet => XelibBodyOpcode.DeferredGenericPropertySet,
        BoundDeferredGenericOperationKind.IndexerGet => XelibBodyOpcode.DeferredGenericIndexerGet,
        BoundDeferredGenericOperationKind.IndexerSet => XelibBodyOpcode.DeferredGenericIndexerSet,
        BoundDeferredGenericOperationKind.Construction => XelibBodyOpcode.DeferredGenericConstruction,
        BoundDeferredGenericOperationKind.Allocation => XelibBodyOpcode.DeferredGenericAllocation,
        _ => throw Invalid($"deferred generic operation '{value}' cannot be represented"),
    };

    private static BoundDeferredGenericOperationKind MapDeferred(XelibBodyOpcode value) => value switch
    {
        XelibBodyOpcode.DeferredGenericFunctionCall => BoundDeferredGenericOperationKind.FunctionCall,
        XelibBodyOpcode.DeferredGenericFieldGet => BoundDeferredGenericOperationKind.FieldGet,
        XelibBodyOpcode.DeferredGenericFieldSet => BoundDeferredGenericOperationKind.FieldSet,
        XelibBodyOpcode.DeferredGenericPropertyGet => BoundDeferredGenericOperationKind.PropertyGet,
        XelibBodyOpcode.DeferredGenericPropertySet => BoundDeferredGenericOperationKind.PropertySet,
        XelibBodyOpcode.DeferredGenericIndexerGet => BoundDeferredGenericOperationKind.IndexerGet,
        XelibBodyOpcode.DeferredGenericIndexerSet => BoundDeferredGenericOperationKind.IndexerSet,
        XelibBodyOpcode.DeferredGenericConstruction => BoundDeferredGenericOperationKind.Construction,
        XelibBodyOpcode.DeferredGenericAllocation => BoundDeferredGenericOperationKind.Allocation,
        _ => throw Invalid($"opcode '{value}' is not a deferred generic operation"),
    };
}
