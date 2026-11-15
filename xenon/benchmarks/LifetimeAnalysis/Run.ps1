param(
    [Parameter(Mandatory=$true)][string]$CompilerPath,
    [Parameter(Mandatory=$true)][string]$ResultsDirectory,
    [ValidatePattern('^[a-zA-Z0-9_-]+$')][string]$Label = 'current',
    [ValidateRange(1,100)][int]$Repeats = 3,
    [string[]]$Scenarios = @('frees10','frees50','frees130','frees250','frees500','frees1000','chain70','nested','recursive','deep','async'),
    [ValidateRange(1,7200)][int]$ScenarioTimeoutSeconds = 600
)
$ErrorActionPreference = 'Stop'
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$results = [System.IO.Path]::GetFullPath($ResultsDirectory)
$output = Join-Path $PSScriptRoot "bin/$Label/"
New-Item -ItemType Directory -Force -Path $results | Out-Null
$env:MSBuildEnableWorkloadResolver = 'false'
dotnet build (Join-Path $PSScriptRoot 'Benchmark.csproj') -c Release -m:1 -nr:false "-p:CompilerPath=$compiler" "-p:OutputPath=$output"
if ($LASTEXITCODE -ne 0) { throw 'Benchmark build failed' }
Get-FileHash -LiteralPath $compiler -Algorithm SHA256 | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $results "$Label-compiler.json")
$failed = $false
foreach ($scenario in $Scenarios) {
    if ($scenario -notmatch '^[a-zA-Z0-9_-]+$') { throw 'Invalid scenario name' }
    $json = Join-Path $results "$Label-$scenario.json"
    $progress = Join-Path $results "$Label-$scenario-progress.jsonl"
    $process = Start-Process -FilePath (Get-Command dotnet).Source -ArgumentList @(('"' + (Join-Path $output 'Benchmark.dll') + '"'),$scenario,$Repeats) -WindowStyle Hidden -PassThru -RedirectStandardOutput $json -RedirectStandardError $progress
    if (-not $process.WaitForExit($ScenarioTimeoutSeconds * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        @{scenario=$scenario;status='timeout';timeoutSeconds=$ScenarioTimeoutSeconds;progressFile=[IO.Path]::GetFileName($progress)} | ConvertTo-Json | Set-Content -LiteralPath $json
        $failed = $true
    } elseif ($process.ExitCode -ne 0) { $failed = $true }
    Write-Output "$Label $scenario completed; exit=$($process.ExitCode)"
}
if ($failed) { throw 'One or more scenarios failed or exceeded the recorded time limit; inspect JSON/progress files.' }
