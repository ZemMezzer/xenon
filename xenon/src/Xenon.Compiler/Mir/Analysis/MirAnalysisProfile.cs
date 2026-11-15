using System.Diagnostics;

namespace Xenon.Compiler.Mir.Analysis;

// Opt-in benchmark instrumentation. No observer, timestamps, allocation counters
// or measurement objects in ordinary compilations. Thread-local observers keep
// parallel compiler/test sessions independent; this is not program runtime code.
internal sealed class MirAnalysisProfile : IDisposable
{
    [ThreadStatic] public static Action<string, double, long, long, int, int>? Observer;
    private readonly Action<string, double, long, long, int, int> _observer;
    private readonly string _name;
    private readonly long _start = Stopwatch.GetTimestamp();
    private readonly long _allocated = GC.GetAllocatedBytesForCurrentThread();
    private readonly int _blocks, _statements;
    public long Iterations;
    private MirAnalysisProfile(string name, MirFunction? function, Action<string, double, long, long, int, int> observer)
    {
        _name = name; _observer = observer;
        _blocks = function?.Blocks.Length ?? 0;
        _statements = function?.Blocks.Sum(block => block.Statements.Length) ?? 0;
    }
    public static MirAnalysisProfile? Start(string name, MirFunction? function = null) =>
        Observer is { } observer ? new(name, function, observer) : null;
    public void Dispose() => _observer(_name, Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
        GC.GetAllocatedBytesForCurrentThread() - _allocated, Iterations, _blocks, _statements);
}
