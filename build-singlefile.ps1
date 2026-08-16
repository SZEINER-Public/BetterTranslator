[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [ValidateSet('store', 'xpress-huff', 'lzms')]
    [string]$Algorithm = 'lzms',
    [string]$CertificateThumbprint,
    [string]$CertificatePath,
    [string]$CertificatePassword,
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$targets = Join-Path $root 'build\SingleFile.targets'
$stageRoot = Join-Path $root "obj\single-file\$Configuration"
$logDirectory = Join-Path $stageRoot 'logs'

function Fail([string]$message) {
    Write-Host "  FAILED  $message" -ForegroundColor Red
    exit 1
}

function Step([string]$message) {
    Write-Host ""
    Write-Host "== $message" -ForegroundColor Cyan
}

function Invoke-Windowless([string]$file, [string[]]$argumentList, [string]$logName) {
    $stdout = Join-Path $logDirectory "$logName.out.log"
    $stderr = Join-Path $logDirectory "$logName.err.log"
    $runner = Join-Path $logDirectory "$logName.cmd"

    Set-Content -Path $runner -Encoding ascii -Value @"
@echo off
"$file" $($argumentList -join ' ') > "$stdout" 2> "$stderr"
exit /b %ERRORLEVEL%
"@

    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $env:ComSpec
    $start.Arguments = "/c `"$runner`""
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true

    $process = [System.Diagnostics.Process]::Start($start)
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        Get-Content $stdout -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" }
        Get-Content $stderr -Tail 40 -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
        Fail "$logName exited $($process.ExitCode). Full output in $stdout"
    }

    return $stdout
}

Step 'Checking prerequisites'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Fail 'The .NET SDK is not installed, or dotnet is not on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}

$ten = @(& dotnet --list-sdks | Where-Object { $_ -match '^10\.' })
if (-not $ten.Count) {
    Fail 'No .NET 10 SDK found. The payload targets net10.0-windows10.0.19041.0.'
}
Write-Host "  dotnet SDK: $($ten[0])" -ForegroundColor Green

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    Fail 'vswhere.exe was not found. The bootstrap is C++, so this build needs Visual Studio with the "Desktop development with C++" workload.'
}

$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    Fail 'No Visual Studio install with the C++ toolset was found. Install the "Desktop development with C++" workload.'
}
Write-Host "  C++ toolchain: $vs" -ForegroundColor Green

if (-not (Test-Path $targets)) {
    Fail "The build targets were not found at $targets"
}

New-Item -ItemType Directory -Force -Path $logDirectory | Out-Null

Step "Building the single file artifact ($Configuration, $Algorithm)"

$msbuildArguments = @(
    "`"$targets`""
    '-target:SingleFile'
    '-nologo'
    '-verbosity:minimal'
    '-nodeReuse:false'
    "-p:SingleFileConfiguration=$Configuration"
    "-p:SingleFileAlgorithm=$Algorithm"
)

if ($CertificateThumbprint) { $msbuildArguments += "-p:SingleFileCertificateThumbprint=$CertificateThumbprint" }
if ($CertificatePath) { $msbuildArguments += "`"-p:SingleFileCertificatePath=$CertificatePath`"" }
if ($CertificatePassword) { $msbuildArguments += "`"-p:SingleFileCertificatePassword=$CertificatePassword`"" }
if ($SkipSelfTest) { $msbuildArguments += '-p:SingleFileSkipSelfTest=true' }

if (-not ($CertificateThumbprint -or $CertificatePath)) {
    Write-Host "  no certificate configured, the artifact will not be signed" -ForegroundColor Yellow
}

$log = Invoke-Windowless 'dotnet' (@('msbuild') + $msbuildArguments) 'singlefile'
Get-Content $log | Select-Object -Last 25 | ForEach-Object { Write-Host "    $_" }

Step 'Result'

$artifact = Join-Path $root "bin\$Configuration\BetterTranslator.exe"
if (-not (Test-Path $artifact)) {
    Fail "The build reported success but $artifact is not there"
}

$shipped = @(Get-ChildItem -Path (Split-Path $artifact) -Force)
if ($shipped.Count -ne 1) {
    Fail "The release directory holds $($shipped.Count) entries. Exactly one file ships."
}

Write-Host ("  {0:N0} bytes  {1}" -f (Get-Item $artifact).Length, $artifact) -ForegroundColor Green

$report = Join-Path $stageRoot 'pack-report.txt'
if (Test-Path $report) {
    Get-Content $report | Select-Object -First 14 | ForEach-Object { Write-Host "  $_" }
}

Write-Host ""
Write-Host "== Done" -ForegroundColor Green
