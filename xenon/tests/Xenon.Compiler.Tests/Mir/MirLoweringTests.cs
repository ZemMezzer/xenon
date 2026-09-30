using Xenon.Compiler.Mir;
using Xenon.Compiler.Mir.Lowering;
using Xenon.Compiler.Semantics.Binding;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Mir;

public sealed class MirLoweringTests
{
    [Theory]
    [InlineData("int Main() { int x = 2; return x + (x = 5); }", 7)]
    [InlineData("int Main() { int x = 2; return x++ + ++x; }", 6)]
    [InlineData("int Main() { int sum = 0; for (int i = 0; i < 6; i++) { if (i == 2) continue; if (i == 5) break; sum += i; } return sum; }", 8)]
    [InlineData("int Main() { int x = 0; while (x < 3) { x++; } return x; }", 3)]
    [InlineData("int Main() { int x = 0; if (false && ++x > 0) return 9; if (true || ++x > 0) x += 2; return x; }", 2)]
    [InlineData("int Main() { int x = 0; if (true && ++x > 0) x += 2; return x; }", 3)]
    [InlineData("int Main() { int x = 2; switch (x) { case 1: return 10; case 2: return 20; default: return 30; } }", 20)]
    [InlineData("int Main() { int x = 2; switch (x) { case 1: case 2: return 20; default: return 30; } }", 20)]
    [InlineData("int Main() { int x = 0; while (x < 4) { switch (x) { case 2: break; default: x++; continue; } break; } return x; }", 2)]
    [InlineData("int Add(int a, int b) { return a + b; } int Main() { int x = 2; return Add(x, x = 5); }", 7)]
    [InlineData("int Main() { int x = 1; x += (x = 3); return x; }", 4)]
    public void LoweredCfgPreservesEvaluationAndControlFlow(string source, int expected)
    {
        var functions = Lower(source);
        Assert.Equal(expected, Execute(functions, functions.Single(f => f.Symbol.Name == "Main"), []));
    }

    [Theory]
    [InlineData("int Main() { int x = 0; try { x = 1; } finally { x += 2; } return x; }", 3)]
    [InlineData("int Main() { int x = 1; try { return x; } finally { x = 8; } }", 1)]
    [InlineData("int Main() { try { return 1; } finally { return 2; } }", 2)]
    [InlineData("int Main() { int x = 0; try { throw 1; } catch (...) { x = 2; } finally { x += 3; } return x; }", 5)]
    [InlineData("int Main() { try { throw 3; } catch (readonly int& error) { return error; } }", 3)]
    [InlineData("int Main() { try { throw 3; } finally { return 8; } }", 8)]
    [InlineData("int Main() { int x = 0; while (x < 3) { try { x++; continue; } finally { x++; } } return x; }", 4)]
    [InlineData("int Main() { int x = 0; while (true) { try { break; } finally { x = 7; } } return x; }", 7)]
    [InlineData("int Main() { int x = 0; try { try { throw 1; } catch (...) { throw 2; } finally { x = 3; } } catch (...) { x += 4; } return x; }", 7)]
    [InlineData("int Main() { int x = 0; try { try { throw 1; } catch (...) { throw; } finally { x = 3; } } catch (...) { x += 4; } return x; }", 7)]
    [InlineData("int Main() { try { try { throw 1; } finally { throw 9; } } catch (readonly int& error) { return error; } }", 9)]
    [InlineData("int Main() { try { throw 1; } catch (...) { try { throw; } catch (...) { return 7; } } }", 7)]
    [InlineData("int F() { throw 5; } int Main() { try { return F(); } catch (readonly int& error) { return error; } }", 5)]
    public void ExceptionCfgPreservesFinalizersAndPropagation(string source, int expected)
    {
        MirFunction[] functions = Lower(source);
        var records = new List<ExceptionRecord>();
        Assert.Equal(expected, Execute(functions, functions.Single(f => f.Symbol.Name == "Main"), [], records));
        Assert.Empty(records);
    }

    [Fact]
    public void DirectAndIndirectCallsHaveExplicitUnwindAndNormalBlocks()
    {
        var functions = Lower("int F(int x) { return x; } int Main() { function int(int)* p = &F; return p(F(5)); }");
        MirFunction main = functions.Single(f => f.Symbol.Name == "Main");
        MirCall[] calls = main.Blocks.Select(b => b.Terminator).OfType<MirCall>().ToArray();
        Assert.Equal(2, calls.Length);
        foreach (MirCall call in calls)
        {
            Assert.NotEqual(call.Normal, call.Unwind);
            Assert.IsType<MirResumeUnwind>(main.Blocks.Single(b => b.Id == call.Unwind).Terminator);
        }
    }

    [Theory]
    [InlineData("struct Item { public int x; public Item(int value) { x = value; } public int Read() { return x; } } int Main() { Item a = Item(4); return a.Read(); }")]
    [InlineData("int Main() { int[] a = new int[3]; a[1] = 7; return a.Length + a[1]; }")]
    [InlineData("int Main() { int x = 1; int* p = &x; *p = 2; return x; }")]
    [InlineData("int Main() { int[,] a = new int[2,3]; a[1,2] = 7; return a[1,2]; }")]
    [InlineData("struct Item { public int x = 7; } int Main() { Item a = Item(); return a.x; }")]
    public void AggregateArrayAndPointerOperationsLowerAndVerify(string source)
    {
        MirFunction[] functions = Lower(source);
        Assert.All(functions, function => Assert.Empty(MirVerifier.Verify(function)));
        Assert.All(functions, function => Assert.NotEmpty(MirPrinter.Dump(function)));
    }

    [Fact]
    public void DefaultAggregateConstructionCallsTheHiddenFieldInitializer()
    {
        MirFunction[] functions = Lower("struct Item { public int x = 7; } int Main() { Item a = Item(); return a.x; }");
        MirFunction main = functions.Single(f => f.Symbol.Name == "Main");
        Assert.Contains(main.Blocks.Select(b => b.Terminator).OfType<MirCall>(),
            call => call.Callee is MirFunctionOperand { Function.FunctionKind: FunctionKind.InstanceInitializer });
    }

    [Fact]
    public void ReplacementWritesRetainTheirCleanupIntent()
    {
        MirFunction main = Lower("int Main() { int x = 1; x = 2; return x; }").Single();
        MirLocal x = main.Locals.Single(local => local.Name == "x");
        MirAssign[] writes = main.Blocks.SelectMany(block => block.Statements).OfType<MirAssign>()
            .Where(assign => assign.Destination.Local == x.Id).ToArray();
        Assert.Equal([MirWriteKind.Initialize, MirWriteKind.Replace], writes.Select(write => write.WriteKind));
    }

    [Fact]
    public void ExpressionLocationsSurviveLowering()
    {
        Compilation compilation = Compile("int Main() { return 1 + 2; }");
        BoundFunction bound = compilation.SemanticModel.Functions.Single();
        BoundExpression expression = Assert.IsType<BoundReturnStatement>(bound.Body.Statements.Single()).Expression!;
        var location = new TextLocation(SourceText.From("a + b", "expression.xe"), new(0, 5));
        MirFunction function = MirLowerer.Lower(bound, compilation.SemanticModel.TypeFactory,
            new Dictionary<BoundExpression, TextLocation>(ReferenceEqualityComparer.Instance) { [expression] = location });
        Assert.Contains(function.Blocks.SelectMany(b => b.Statements), statement => statement.Source.Location == location);
        Assert.Contains(function.Blocks, block => block.Terminator is MirReturn && block.Terminator.Source.Location == location);
    }

    [Fact]
    public void LoweringHonorsCancellation()
    {
        Compilation compilation = Compile("int Main() { return 1; }");
        Assert.Throws<OperationCanceledException>(() => MirLowerer.Lower(compilation.SemanticModel.Functions.Single(),
            compilation.SemanticModel.TypeFactory, cancellation: new CancellationToken(true)));
    }

    private static Compilation Compile(string text)
    {
        Compilation compilation = Compilation.Create(SourceText.From("namespace MirTests; " + text));
        Assert.False(compilation.HasErrors, string.Join(Environment.NewLine, compilation.Diagnostics));
        return compilation;
    }

    private static MirFunction[] Lower(string source)
    {
        Compilation compilation = Compile(source);
        return compilation.SemanticModel.Functions.Select(f => MirLowerer.Lower(f, compilation.SemanticModel.TypeFactory)).ToArray();
    }

    // A deliberately small executable oracle for scalar lowering tests. It consumes
    // MIR only, so high-level nodes or incorrectly ordered loads cannot hide in tests.
    private static object? Execute(MirFunction[] functions, MirFunction function, object?[] args, List<ExceptionRecord>? exceptions = null)
    {
        exceptions ??= [];
        var locals = new Dictionary<MirLocalId, object?>();
        MirLocal[] parameters = function.Locals.Where(l => l.Kind == MirLocalKind.Parameter).ToArray();
        for (int i = 0; i < args.Length; i++) locals[parameters[i].Id] = args[i];
        object? Operand(MirOperand value) => value switch
        {
            MirConstant literal => literal.Value,
            MirCopy copy when copy.Place.Projections.IsEmpty => locals[copy.Place.Local],
            MirCopy copy when copy.Place.Projections is [MirDerefProjection] => ((ExceptionRecord)locals[copy.Place.Local]!).Value,
            MirMove move when move.Place.Projections.IsEmpty => locals[move.Place.Local],
            MirFunctionOperand target => target.Function,
            _ => throw new NotSupportedException(value.ToString()),
        };
        object? RValue(MirRValue value) => value switch
        {
            MirUse use => Operand(use.Operand),
            MirBinary binary => Binary(binary.Operator, Operand(binary.Left), Operand(binary.Right)),
            MirUnary { Operator: MirUnaryOperator.Negate } unary => -Convert.ToInt32(Operand(unary.Operand)),
            MirUnary { Operator: MirUnaryOperator.Not } unary => !Convert.ToBoolean(Operand(unary.Operand)),
            MirDefault => 0,
            MirCurrentException => exceptions.Last(),
            MirExceptionMatches match => TypeIdentity.AreSame(((ExceptionRecord)Operand(match.Record)!).Type, match.ExceptionType),
            MirExceptionReference reference => Operand(reference.Record),
            _ => throw new NotSupportedException(value.ToString()),
        };
        MirBlockId current = function.Entry;
        for (int steps = 0; steps < 10000; steps++)
        {
            MirBasicBlock block = function.Blocks.Single(b => b.Id == current);
            foreach (MirStatement statement in block.Statements)
                switch (statement)
                {
                    case MirAssign assign:
                        Assert.Empty(assign.Destination.Projections);
                        locals[assign.Destination.Local] = RValue(assign.Value);
                        break;
                    case MirStorageLive live: locals.Remove(live.Local); break;
                    case MirStorageDead dead: locals.Remove(dead.Local); break;
                    case MirReleaseException release:
                        exceptions.Remove((ExceptionRecord)Operand(release.Record)!);
                        break;
                    default: throw new NotSupportedException(statement.ToString());
                }
            switch (block.Terminator)
            {
                case MirReturn ret: return ret.Value is null ? null : Operand(ret.Value);
                case MirGoto go: current = go.Target; break;
                case MirSwitch selection:
                    object? value = Operand(selection.Value);
                    current = selection.Cases.FirstOrDefault(c => Equals(c.Value.Value, value))?.Target ?? selection.Otherwise;
                    break;
                case MirCall call:
                    FunctionSymbol callee = Assert.IsType<FunctionSymbol>(Operand(call.Callee));
                    try
                    {
                        object? result = Execute(functions, functions.Single(f => f.Symbol == callee), call.Arguments.Select(Operand).ToArray(), exceptions);
                        if (call.Destination is not null) locals[call.Destination.Local] = result;
                        current = call.Normal;
                    }
                    catch (PropagatedException) { current = call.Unwind; }
                    break;
                case MirThrow throwing:
                    if (throwing.Exception is { } thrown) exceptions.Add(new(Operand(thrown), thrown.Type));
                    current = throwing.Unwind;
                    break;
                case MirResumeUnwind: throw new PropagatedException();
                default: throw new NotSupportedException(block.Terminator.ToString());
            }
        }
        throw new InvalidOperationException("MIR test did not terminate.");
    }

    private sealed record ExceptionRecord(object? Value, TypeSymbol Type);
    private sealed class PropagatedException : Exception;

    private static object Binary(MirBinaryOperator op, object? left, object? right)
    {
        int a = Convert.ToInt32(left), b = Convert.ToInt32(right);
        return op switch
        {
            MirBinaryOperator.Add => a + b,
            MirBinaryOperator.Subtract => a - b,
            MirBinaryOperator.Multiply => a * b,
            MirBinaryOperator.Equal => a == b,
            MirBinaryOperator.NotEqual => a != b,
            MirBinaryOperator.Less => a < b,
            MirBinaryOperator.LessOrEqual => a <= b,
            MirBinaryOperator.Greater => a > b,
            MirBinaryOperator.GreaterOrEqual => a >= b,
            _ => throw new NotSupportedException(op.ToString()),
        };
    }
}
