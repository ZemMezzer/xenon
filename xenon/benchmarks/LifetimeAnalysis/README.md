# Lifetime analysis benchmark

Requires .NET 10 and a separately built Xenon.Compiler Release DLL. There is no
project reference: a preserved old compiler can be compared with the current one.

From the repository root:

```powershell
./xenon/benchmarks/LifetimeAnalysis/Run.ps1 -CompilerPath ./build/local/xenon/bin/Xenon.Compiler/Release/net10.0/Xenon.Compiler.dll -ResultsDirectory ./build/lifetime-measurements -Label after
```

Use a different label and preserved DLL for the baseline. Run the two versions on
the same idle host, sequentially. Each of eleven scenarios runs in a separate
process with a full scenario warm-up and three GC-separated measured samples. The harness
checks expected diagnostic IDs so silently missing a violation does not count as
a performance win. `-Repeats` changes the repetition count.

The preserved slow baseline needs a larger limit for 1000 frees:

```powershell
./xenon/benchmarks/LifetimeAnalysis/Run.ps1 -CompilerPath ./build/borrow-hardening/before/Xenon.Compiler.dll -ResultsDirectory ./build/lifetime-baseline -Label before -ScenarioTimeoutSeconds 7200
```

Output records wall and CPU milliseconds, managed allocation traffic, peak process working set,
CFG blocks/statements, destruction effect count, runtime/OS/processor count and compiler SHA-256. Peak working set is a high-water
mark across all samples in that process; allocated bytes are not resident memory.
Tiered JIT and GC can still affect measured samples. Keep all samples and report
medians and ranges, not only the fastest sample.

`-Scenarios` selects a subset. The default per-process limit is 600 seconds,
including warm-up; `-ScenarioTimeoutSeconds` changes it. A timed-out process is
terminated and recorded explicitly, with completed warm-up/sample records retained
in `*-progress.jsonl`. A timeout is not a passing sample or a measured median.

For phase profiles, run the built benchmark directly with `--profile` after the
scenario name and repeat count. Supported compiler versions expose an internal
thread-local observer. Profiles include inclusive phase time, allocation traffic
and dataflow processed-block counts. They are separate from observer-disabled
performance comparisons. Older compilers remain usable and report
`profileAvailable: false`. MIR shape counting happens after the measured samples.
Processed-block counts count transfer work, not SCC fixed-point rounds. Destruction
effect counts include exported operation and exit records, not runtime free calls;
ten independent frees can therefore produce twenty effect records. Final phase
counts are preserved for all eleven scenarios; the instrumented pre-optimization
profile isolates the 130-free bottleneck separately from the older baseline DLL.

The checked-in `results/hardening` directory records the final-hardening iteration;
the parent results directory preserves the earlier stabilization baseline. The
1000-free baseline needed a 7200-second process limit after the default 600-second
run timed out during warm-up. Its extended rerun overlapped acceptance tests and
is explicitly marked non-isolated; do not present its wall ratio as a controlled
speedup. All final measurements and the other baseline scenarios run separately
from test workloads. The companion `../LifetimeDiagnostics` runner compares
diagnostic IDs and source positions across preserved binaries. Detailed
analysis and limitations: [LifetimePerformance.md](../../src/Xenon.Compiler/Mir/Analysis/LifetimePerformance.md).
