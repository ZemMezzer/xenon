# Lifetime diagnostic differential corpus

This unchanged public-API runner compares preserved compiler binaries on thirteen
cases: valid aliases, use after free, conditional invalid/valid calls, double free,
placement, active and independent loans, invalid ownership transfer, multiple
targets, recursion, unknown raw effects and metadata-only imported destruction.
It records sorted error IDs and source starts and fails on unexpected categories.
Compare the complete `cases` arrays, not only the exit status.

```powershell
$compiler = (Resolve-Path ./build/borrow-hardening/before/Xenon.Compiler.dll).Path
dotnet build ./xenon/benchmarks/LifetimeDiagnostics/Diagnostics.csproj -c Release -m:1 -nr:false "-p:CompilerPath=$compiler" -p:OutputPath=bin/baseline
dotnet ./xenon/benchmarks/LifetimeDiagnostics/bin/baseline/Diagnostics.dll
```

Repeat with a different compiler path/output label. Baseline DLLs are local ignored
artifacts, not checked-in dependencies. The final-hardening JSON results accompany
the performance results. The normal compiler CI suite contains the adversarial
regressions and eleven exact effect-set comparisons with the shared storage-exit
proof enabled and disabled; this runner supplements those tests across binaries.
