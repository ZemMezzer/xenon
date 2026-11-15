# Lifetime-analysis performance

## Method

The reproducible harness is `xenon/benchmarks/LifetimeAnalysis/Program.cs`, run by
`Run.ps1`. It compiles source through `Compilation.Create(...).HasErrors`, verifies
the expected diagnostic identifiers, and records elapsed time, total managed bytes
allocated and process peak working set. Each scenario runs in its own process,
performs a small compiler warm-up, and takes three samples with full GC before each.
The first sample still includes scenario-specific JIT work; medians and ranges are
reported rather than treating one sample as a stable result.

Host: AMD Ryzen 9 5900X, 12 cores / 24 logical processors; Windows 10.0.26200 x64;
.NET SDK 10.0.302, runtime 10.0.10. Both compared compiler binaries use Release.
Before measurements used the preserved, previously accepted compiler at
`build/borrow-stabilization/before/Xenon.Compiler.dll`, SHA-256
`57E1F8A81C152A0A5EA6CBF03DF78AD95A12A4419E5772AAA20CA4793DBDBBAD`.
The benchmark has no native compilation/linking. Full test suites are not run
concurrently with the reported benchmark samples. Other host activity, JIT and GC
still cause noise; small differences are not evidence of a stable improvement.

Peak working set is the process high-water mark, including warm-up and previous
samples of the same scenario. Managed allocated bytes are cumulative allocation
traffic during one sample, not live memory. These are deliberately separate metrics.

## Profile before optimization

Temporary phase instrumentation of the preserved implementation, removed before
production builds, recorded the 130-free scenario plus its small warm-up:

| Inclusive phase | Calls | Time | Managed allocation |
| --- | ---: | ---: | ---: |
| Entire pointer phase | 2 | 17.908 s | 8.378 GB |
| SummarizeEffects | 10 | 17.159 s | 8.258 GB |
| SupportedPath | 520 | 9.930 s | 6.104 GB |
| DeathWitness dataflow | 261 | 6.845 s | 1.998 GB |
| CFG construction | 364 | 0.209 s | 0.139 GB |
| Reference provenance dataflow | 15 | 0.262 s | 0.036 GB |

Times are inclusive and must not be summed. This instrumentation used the local
Profile configuration (unoptimized), so its absolute times are not comparable to
the Release benchmark. Counts and allocation distribution identified repeated
reverse fixed-point scans and witness transfers as the dominant work; CFG building
and provenance alone were not the principal bottleneck.

## Changes justified by the profile

- Reverse reachability follows predecessor edges once, replacing repeated scans
  of the entire function until no predecessor can be added.
- Immutable entry-flow edge feasibility is cached lazily across target queries.
  Witness-local maps are query-local; eager caching of complete witness graphs was
  rejected after measurement increased cost and peak memory.
- Acyclic SCCs need one summary pass after their callees, not a second unchanged
  pass. Recursive components retain fixed-point convergence checks.
- Storage death witnesses skip unused per-statement Before/After snapshots.
  Object reconstruction witnesses keep them where ordering queries need them.

Frozen provenance is reused within a function analysis, as are the existing
per-death use witnesses. Different destruction seeds are not merged: batching them
without retaining their conditions and ordering could turn may-alias into a raw
proof. CFG construction itself was small in the profile; restructuring the shared
graph representation was not justified by this measurement.

## Repeated Release comparison

| Scenario | Before median (range), ms | After median (range), ms | Before / after |
| --- | ---: | ---: | ---: |
| frees10 | 112.96 (95.5079-164.0637) | 58.68 (55.1511-126.9909) | 1.92x |
| frees50 | 720.18 (669.4268-1379.1528) | 436.01 (296.7063-924.5501) | 1.65x |
| frees130 | 8936.86 (8782.3413-10692.6561) | 3020.52 (2192.8484-4782.4756) | 2.96x |
| frees250 | 69106.56 (67485.909-70336.4594) | 16375.09 (16067.976-17905.5978) | 4.22x |
| chain70 | 186.55 (170.9661-218.4732) | 164.65 (137.8428-218.9579) | 1.13x |
| nested | 42.90 (40.479-137.5614) | 37.55 (33.7263-133.1929) | 1.14x |
| recursive | 21.46 (20.22-91.0603) | 21.78 (20.2941-95.6711) | 0.98x |
| async | 67.51 (65.578-230.7444) | 70.96 (68.9699-228.2393) | 0.95x |

| Scenario | Before allocated MB | After allocated MB | Before peak MiB | After peak MiB |
| --- | ---: | ---: | ---: | ---: |
| frees10 | 20.43 | 11.17 | 68.41 | 62.91 |
| frees50 | 621.62 | 131.67 | 89.05 | 80.02 |
| frees130 | 8245.38 | 1131.75 | 125.05 | 105.71 |
| frees250 | 53864.51 | 5688.96 | 171.22 | 160.06 |
| chain70 | 37.72 | 32.38 | 70.24 | 68.54 |
| nested | 9.24 | 7.79 | 61.49 | 59.51 |
| recursive | 5.02 | 4.65 | 53.91 | 53.24 |
| async | 15.99 | 16.00 | 69.03 | 68.80 |

The 130/250-free medians improve by 2.96x / 4.22x; all three final wide-case samples are faster than every baseline sample. At 250 frees, cumulative managed allocation falls from 53.86 GB to 5.69 GB (9.47x), while peak working set falls from 171.22 MiB to 160.06 MiB. These are different memory metrics.

Recursive and async median changes are 1.53% and 5.10% respectively (positive means slower). Three samples do not establish whether small-case differences come from changed code or JIT/host variation; no general speedup is claimed for them. The call-chain and nested-condition ranges also overlap.

Final compiler SHA-256: `CB6FD7232057B368AF78905D4E1C4C88EAC2C950F3F4C37F5BE4066902175B07`. The before/after harness source is byte-identical. Raw samples and the derived table are under `xenon/benchmarks/LifetimeAnalysis/results/`.

## Interpretation and remaining cost

Wide functions still construct separate DeathWitness dataflow solutions. Flow
states contain growing scalar/predicate maps, so the 250-free case remains
superlinear and is not a constant-cost or linear-time analysis. The work reduces
repeated reachability and snapshot traffic without removing exact path witnesses.
Small recursive/async differences must be read within run noise, not described as
universal speedups. Native E2E wall times include unrelated code generation,
linking and concurrent test scheduling and do not isolate this analysis.

To reproduce with the preserved baseline and a current Release compiler:

```powershell
./xenon/benchmarks/LifetimeAnalysis/Run.ps1 -CompilerPath ./build/borrow-stabilization/before/Xenon.Compiler.dll -ResultsDirectory ./build/lifetime-rerun -Label before
./xenon/benchmarks/LifetimeAnalysis/Run.ps1 -CompilerPath ./build/local/xenon/bin/Xenon.Compiler/Release/net10.0/Xenon.Compiler.dll -ResultsDirectory ./build/lifetime-rerun -Label after
```

The baseline DLL is a local ignored artifact, not a repository dependency. JSON
results, compiler hashes and the profile accompany the benchmark sources.
