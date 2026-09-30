using Xenon.Compiler.Mir;
using Xenon.Compiler.Semantics.Symbols;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirCleanupTests
{
    private const string Types = """
        struct A { public ~A() {} }
        struct B { public ~B() {} }
        struct Pair { public A first; public B second; }
        """;

    [Theory]
    [InlineData("A a = A(); { B b = B(); } return 0;", "B,A")]
    [InlineData("A a = A(); B b = B(); return 0;", "B,A")]
    [InlineData("A a = A(); A b = move a; return 0;", "A")]
    [InlineData("Pair p = Pair(); A a = move p.first; return 0;", "A,B")]
    [InlineData("Pair p = Pair(); A a = move p.first; p.first = A(); return 0;", "A,B,A")]
    [InlineData("A a = A(); a = A(); return 0;", "A,A")]
    [InlineData("for (int i = 0; i < 2; i++) { A a = A(); continue; } return 0;", "A,A")]
    [InlineData("while (true) { A a = A(); break; } return 0;", "A")]
    [InlineData("try { A a = A(); throw 1; } catch (...) { B b = B(); } return 0;", "A,B")]
    [InlineData("try { A a = A(); return 0; } finally { B b = B(); }", "A,B")]
    [InlineData("A a; B b; b = B(); a = A(); return 0;", "A,B")]
    [InlineData("A a; B b; a = A(); b = B(); return 0;", "B,A")]
    [InlineData("storage<A> slot; return 0;", "")]
    [InlineData("storage<A> slot; slot = A(); return 0;", "A")]
    [InlineData("storage<A> slot; slot = A(); A a = move slot; return 0;", "A")]
    [InlineData("pin<A> a = A(); return 0;", "A")]
    [InlineData("pin<storage<A>> slot; slot = A(); return 0;", "A")]
    [InlineData("A a = A(); B[] b = B[2]; A c = A(); return 0;", "A,B,B,A")]
    [InlineData("A[] a = A[0]; return 0;", "")]
    [InlineData("A[] a = A[2]; A[] b = move a; return 0;", "A,A")]
    [InlineData("A[] a = A[1]; a = A[2]; return 0;", "A,A,A")]
    public void LifetimesEndOnceInReverseOrder(string body, string expected)
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types + "int Main() { " + body + " }");
        var machine = new Machine(functions);
        Assert.Equal(0, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(expected.Split(',', StringSplitOptions.RemoveEmptyEntries), machine.Drops);
    }

    [Fact]
    public void ByValueParameterOwnsCommittedMove()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "void Take(A a) {} int Main() { A a = A(); Take(move a); return 0; }");
        var machine = new Machine(functions);
        machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []);
        Assert.Equal(["A"], machine.Drops);
    }

    [Fact]
    public void FailedLaterArgumentLeavesEarlierMoveOwnedByItsSource()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "int Fail() { throw 1; } void Take(A a, int x) {} int Main() { A a = A(); try { Take(move a, Fail()); } catch (...) {} return 0; }");
        var machine = new Machine(functions);
        machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []);
        Assert.Equal(["A"], machine.Drops);
    }

    [Fact]
    public void ThrowingDestructorIsNotRetriedDuringUnwind()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "int Main() { try { A a = A(); B b = B(); } catch (...) { return 7; } return 0; }");
        var machine = new Machine(functions) { ThrowFrom = "B" };
        Assert.Equal(7, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["B", "A"], machine.Drops);
    }

    [Fact]
    public void FailedConstructorDestroysOnlyCompletedFields()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types + """
            struct Holder {
                public A first; public B second;
                public Holder() { first = A(); second = B(); throw 1; }
            }
            int Main() { try { Holder value = Holder(); } catch (...) { return 0; } return 1; }
            """);
        var machine = new Machine(functions);
        Assert.Equal(0, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["B", "A"], machine.Drops);
    }

    [Fact]
    public void FinallyDiscardsThePreviousPendingReturn()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "A Make() { try { return A(); } finally { return A(); } } int Main() { A a = Make(); return 0; }");
        var machine = new Machine(functions);
        Assert.Equal(0, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["A", "A"], machine.Drops);
    }

    [Fact]
    public void ArrayUnwindResumesBelowTheThrowingElement()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "int Main() { try { B[] values = B[3]; } catch (...) { return 7; } return 0; }");
        var machine = new Machine(functions) { ThrowAtDrop = 1 };
        Assert.Equal(7, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["B", "B", "B"], machine.Drops);
    }

    [Fact]
    public void FailedHeapArrayInitializationDestroysCompletedPrefixAndFreesAllocation()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower("""
            int Next() { return 1; }
            struct Element { public int Id = Next(); public ~Element() {} }
            int Main() { try { Element[] values = new Element[3]; } catch (...) { return 7; } return 0; }
            """);
        var machine = new Machine(functions) { ThrowWhenCall = "Next", ThrowCallOccurrence = 2 };
        Assert.Equal(7, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["Element"], machine.Drops);
        Assert.Equal(1, machine.FreedBuffers);
    }

    [Fact]
    public void DeleteCompletesArrayCleanupAndFreesBufferWhenAnElementThrows()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types +
            "int Main() { B[] values = new B[3]; try { delete(values); } catch (...) { return 7; } return 0; }");
        var machine = new Machine(functions) { ThrowAtDrop = 1 };
        Assert.Equal(7, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["B", "B", "B"], machine.Drops);
        Assert.Equal(1, machine.FreedBuffers);
    }

    [Fact]
    public void RepeatedConditionAllocationsRemainOwnedUntilTheirScopeEnds()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types + """
            bool Check(B[] values, int n) { return n < 3; }
            int Main() { int i = 0; while ((B[1]).Length + 2 > i++) {} return 0; }
            """);
        var machine = new Machine(functions);
        Assert.Equal(0, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["B", "B", "B", "B"], machine.Drops);
    }

    [Fact]
    public void MethodMoveTransfersTheCallersFieldCleanup()
    {
        MirFunction[] functions = MirFeatureLoweringTests.Lower(Types + """
            struct Holder {
                public A value;
                public A Take() { return move value; }
            }
            int Main() { Holder holder = Holder(); A value = holder.Take(); return 0; }
            """);
        var machine = new Machine(functions);
        Assert.Equal(0, machine.Run(functions.Single(f => f.Symbol.Name == "Main"), []));
        Assert.Equal(["A"], machine.Drops);
    }
    // Executes the lowered graph, including guards and unwind edges. It deliberately
    // knows nothing about source scopes, implicit drops, or ownership inference.
    private sealed class Machine(MirFunction[] functions)
    {
        public List<string> Drops { get; } = [];
        public string? ThrowFrom { get; init; }
        public int ThrowAtDrop { get; init; }
        public string? ThrowWhenCall { get; init; }
        public int ThrowCallOccurrence { get; init; }
        private int _matchingCalls;
        public int FreedBuffers { get; private set; }
        private readonly List<object> _exceptions = [];
        private sealed class Propagate : Exception;
        private sealed class Slot { public object? Value; public bool Initialized; }
        private sealed record Address(Func<object?> Read, Action<object?> Write);

        public object? Run(MirFunction function, object?[] arguments, object? receiver = null)
        {
            var locals = new Dictionary<MirLocalId, object?>();
            foreach (MirLocal local in function.Locals.Where(l => l.Kind == MirLocalKind.Receiver)) locals[local.Id] = receiver;
            MirLocal[] parameters = function.Locals.Where(l => l.Kind == MirLocalKind.Parameter).ToArray();
            for (int i = 0; i < arguments.Length; i++) locals[parameters[i].Id] = arguments[i];
            Address Place(MirPlace place)
            {
                Address address = new(() => locals[place.Local], value => locals[place.Local] = value);
                TypeSymbol type = function.Locals.Single(local => local.Id == place.Local).Type;
                foreach (MirProjection projection in place.Projections)
                {
                    if (projection is MirDerefProjection)
                    {
                        address = (Address)address.Read()!;
                        type = type is PointerTypeSymbol pointer ? pointer.ElementType : ((ReferenceTypeSymbol)type).ElementType;
                    }
                    else if (projection is MirLifetimeProjection)
                    {
                        if (type is StorageTypeSymbol)
                        {
                            var slot = (Slot)address.Read()!;
                            address = new(() => slot.Value, value => slot.Value = value);
                        }
                        type = ((LifetimeModifierTypeSymbol)type).ElementType;
                    }
                    else if (projection is MirLinearIndexProjection index)
                    {
                        var array = (object?[])address.Read()!;
                        int offset = Convert.ToInt32(Operand(index.Index));
                        address = new(() => array[offset], value => array[offset] = value);
                        type = ((ArrayTypeSymbol)type).ElementType;
                    }
                    else if (projection is MirFieldProjection field)
                    {
                        type = field.Field.Type;
                        var fields = (Dictionary<FieldSymbol, object?>)address.Read()!;
                        address = new(() => fields[field.Field], value => fields[field.Field] = value);
                    }
                    else throw new NotSupportedException(projection.ToString());
                }
                return address;
            }
            object? Copy(object? value) => value is Dictionary<FieldSymbol, object?> fields
                ? fields.ToDictionary(pair => pair.Key, pair => Copy(pair.Value)) : value;
            object? Operand(MirOperand operand) => operand switch
            {
                MirConstant constant => constant.Value,
                MirCopy copy => Copy(Place(copy.Place).Read()),
                MirMove move => Copy(Place(move.Place).Read()),
                MirFunctionOperand target => target.Function,
                _ => throw new NotSupportedException(operand.ToString()),
            };
            object? Default(TypeSymbol type) => type switch
            {
                StructTypeSymbol structure => structure.AllInstanceFields.ToDictionary(field => field, field => Default(field.Type)),
                StorageTypeSymbol => new Slot(),
                PinTypeSymbol pin => Default(pin.ElementType),
                _ => 0,
            };
            Address Allocate(TypeSymbol type)
            {
                object? storage = Default(type);
                return new(() => storage, value => storage = value);
            }
            object? RValue(MirRValue value) => value switch
            {
                MirStackAllocation allocation => Allocate(allocation.PointerType.ElementType),
                MirStackSave => new object(),
                MirStorageState state => ((Slot)Place(state.Place).Read()!).Initialized,
                MirUse use => Operand(use.Operand),
                MirDefault zero => Default(zero.Type),
                MirBorrow borrow => Place(borrow.Place),
                MirCast cast => Operand(cast.Operand),
                MirBinary binary => binary.Operator switch
                {
                    MirBinaryOperator.Equal => Equals(Operand(binary.Left), Operand(binary.Right)),
                    MirBinaryOperator.Subtract => Convert.ToInt32(Operand(binary.Left)) - Convert.ToInt32(Operand(binary.Right)),
                    MirBinaryOperator.Add => Convert.ToInt32(Operand(binary.Left)) + Convert.ToInt32(Operand(binary.Right)),
                    MirBinaryOperator.Greater => Convert.ToInt32(Operand(binary.Left)) > Convert.ToInt32(Operand(binary.Right)),
                    MirBinaryOperator.Less => Convert.ToInt32(Operand(binary.Left)) < Convert.ToInt32(Operand(binary.Right)),
                    _ => throw new NotSupportedException(binary.Operator.ToString()),
                },
                MirCurrentException => _exceptions.Last(),
                MirExceptionMatches => true,
                _ => throw new NotSupportedException(value.ToString()),
            };
            MirBlockId current = function.Entry;
            for (int step = 0; step < 10000; step++)
            {
                MirBasicBlock block = function.Blocks.Single(b => b.Id == current);
                foreach (MirStatement statement in block.Statements)
                    switch (statement)
                    {
                        case MirAssign assign: Place(assign.Destination).Write(RValue(assign.Value)); break;
                        case MirStorageLive live: locals.Remove(live.Local); break;
                        case MirStorageDead dead: locals.Remove(dead.Local); break;
                        case MirSetStorageState state: ((Slot)Place(state.Place).Read()!).Initialized = state.Initialized; break;
                        case MirStackRestore: break;
                        case MirForget: break;
                        case MirReleaseException release: _exceptions.Remove(Operand(release.Record)!); break;
                        default: throw new NotSupportedException(statement.ToString());
                    }
                switch (block.Terminator)
                {
                    case MirGoto go: current = go.Target; break;
                    case MirSwitch selection:
                        current = selection.Cases.FirstOrDefault(c => Equals(c.Value.Value, Operand(selection.Value)))?.Target ?? selection.Otherwise;
                        break;
                    case MirReturn ret: return ret.Value is null ? null : Operand(ret.Value);
                    case MirCall call:
                        try
                        {
                            var target = (FunctionSymbol)Operand(call.Callee)!;
                            if (target.Name == ThrowWhenCall && ++_matchingCalls == ThrowCallOccurrence)
                            { _exceptions.Add(1); throw new Propagate(); }
                            object? result = Run(functions.Single(f => f.Symbol == target), call.Arguments.Select(Operand).ToArray(), call.Receiver is null ? null : Operand(call.Receiver));
                            if (call.Destination is { } destination) Place(destination).Write(result);
                            current = call.Normal;
                        }
                        catch (Propagate) { current = call.Unwind; }
                        break;
                    case MirIntrinsicCall intrinsic:
                        if (intrinsic.Intrinsic is MirIntrinsicKind.AllocateStackArray or MirIntrinsicKind.AllocateHeapArray)
                        {
                            Place(intrinsic.Destination!).Write(new object?[intrinsic.Arguments.Select(a => Convert.ToInt32(Operand(a))).Aggregate(1, (a, b) => a * b)]);
                            current = intrinsic.Normal;
                        }
                        else if (intrinsic.Intrinsic == MirIntrinsicKind.ArrayLength)
                        {
                            Place(intrinsic.Destination!).Write(((object?[])Operand(intrinsic.Arguments[0])!).Length);
                            current = intrinsic.Normal;
                        }
                        else if (intrinsic.Intrinsic == MirIntrinsicKind.Free)
                        {
                            FreedBuffers++;
                            current = intrinsic.Normal;
                        }
                        else if (intrinsic.Intrinsic is MirIntrinsicKind.CheckStorageEmpty or MirIntrinsicKind.CheckStorageInitialized)
                        {
                            var slot = (Slot)((Address)Operand(intrinsic.Arguments[0])!).Read()!;
                            Assert.Equal(intrinsic.Intrinsic == MirIntrinsicKind.CheckStorageInitialized, slot.Initialized);
                            current = intrinsic.Normal;
                        }
                        else throw new NotSupportedException(intrinsic.Intrinsic.ToString());
                        break;
                    case MirDrop { Destructor.FunctionKind: FunctionKind.StorageDestructor } drop:
                        try { Run(functions.Single(f => f.Symbol == drop.Destructor), [Place(drop.Place)]); current = drop.Normal; }
                        catch (Propagate) { current = drop.Unwind; }
                        break;
                    case MirDrop drop:
                        string name = drop.Destructor!.ContainingType!.Name;
                        Drops.Add(name);
                        if (name == ThrowFrom || Drops.Count == ThrowAtDrop) { _exceptions.Add(1); current = drop.Unwind; }
                        else current = drop.Normal;
                        break;
                    case MirThrow throwing:
                        if (throwing.Exception is { } exception) _exceptions.Add(Operand(exception)!);
                        current = throwing.Unwind;
                        break;
                    case MirResumeUnwind: throw new Propagate();
                    case MirAbort: throw new InvalidOperationException("Double exception terminated the program.");
                    default: throw new NotSupportedException(block.Terminator.ToString());
                }
            }
            throw new InvalidOperationException("MIR execution exceeded its step budget.");
        }
    }
}
