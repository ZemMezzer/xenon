using Xenon.CodeGen.LLVM;
using Xenon.Compiler.Libraries;
using Xenon.Compiler.Semantics.Symbols;
using Xenon.Compiler.Text;
using Xunit;

namespace Xenon.Compiler.Tests.Libraries;

public sealed class UserDefinedOperatorLibraryTests
{
    [Fact]
    public void SourceFreeOperatorsConversionsAndGenericBodiesRemainReachable()
    {
        Compilation library = Compilation.Create(SourceText.From("""
            namespace Operators;
            public struct Number
            {
                public int Value;
                public static Number operator implicit(int value) { return Number { value }; }
                public static int operator explicit(readonly Number& value) { return value.Value; }
                public static Number operator +(readonly Number& a, readonly Number& b) { return Number { a.Value + b.Value }; }
                public static Number operator -(readonly Number& a) { return Number { -a.Value }; }
                public static Number Add(Number a, Number b) { a += b; return a; }
            }
            public struct Text
            {
                public readonly byte* Raw;
                public static Text operator implicit(readonly byte* value) { return Text { value }; }
            }
            public struct Box<T>
            {
                public T Value;
                public Box(T value) { Value = move value; }
                public static Box<T> operator implicit(T value) { return Box<T>(move value); }
                public static bool operator ==(readonly Box<T>& a, readonly Box<T>& b) { return true; }
            }
            public bool Equal<T>(readonly Box<T>& a, readonly Box<T>& b) { return a == b; }
            """, "unavailable-library.xe"));
        Assert.False(library.HasErrors, string.Join(Environment.NewLine, library.Diagnostics));
        byte[] bytes = XelibWriter.Write(library, new XelibWriteOptions("Operators"));
        var container = XelibContainer.Read(bytes);
        var records = XelibJson.Deserialize<System.Collections.Immutable.ImmutableArray<XelibSymbolRecord>>(
            container.Sections[(uint)XelibSectionKind.Symbols].AsSpan(), null);
        Assert.Contains(records, record => record.OperatorKind == "add");
        Assert.Contains(records, record => record.OperatorKind == "unary_negation");
        Assert.Contains(records, record => record.OperatorKind == "implicit_conversion");
        Assert.Contains(records, record => record.OperatorKind == "explicit_conversion");
        var exports = XelibJson.Deserialize<System.Collections.Immutable.ImmutableArray<XelibExport>>(
            container.Sections[(uint)XelibSectionKind.Exports].AsSpan(), null);
        Assert.True(exports.Select(export => export.Key).Distinct().Count() == exports.Length,
            string.Join(Environment.NewLine, exports.GroupBy(export => export.Key).Where(group => group.Count() > 1).Select(group => group.Key)));
        LibraryCompilationReference reference = XelibReader.Read(bytes);
        Compilation consumer = Compilation.Create(new CompilationOptions(), [reference], SourceText.From("""
            using Operators;
            namespace App;
            int Main()
            {
                Text text = "Hello";
                Number a = 10;
                Number b = 20;
                Number sum = a + b;
                sum = -(-sum);
                sum = Number.Add(sum, a);
                Box<int> first = 1;
                Box<int> second = 2;
                if (Equal<int>(first, second)) return cast<int>(sum);
                return 0;
            }
            """, "app.xe"));
        Assert.False(consumer.HasErrors, string.Join(Environment.NewLine, consumer.Diagnostics));
        var functions = consumer.GetStaticImplementationFunctions().ToArray();
        Assert.Contains(functions, function => function.Symbol.OperatorKind == OperatorKind.ImplicitConversion);
        Assert.Contains(functions, function => function.Symbol.OperatorKind == OperatorKind.Add);
        Assert.Contains(functions, function => function.Symbol.OperatorKind == OperatorKind.Equal &&
            function.Symbol.ContainingStruct is { IsGenericSpecialization: true });
        LlvmTargetOptions target = LlvmTargetOptions.CreateHost();
        Compilation targeted = LlvmIrGenerator.BindForTarget(consumer, target);
        Assert.False(targeted.HasErrors, string.Join(Environment.NewLine, targeted.Diagnostics));
        _ = new LlvmIrGenerator().GenerateForTarget(targeted, target);
    }
}
