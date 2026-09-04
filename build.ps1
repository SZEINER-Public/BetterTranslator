<#
    One command to build, test and publish BetterTranslator from a clean clone.

        pwsh -File build.ps1                 build and test
        pwsh -File build.ps1 -Publish        also produce the distributable exe
        pwsh -File build.ps1 -Parity         also check the port against the
                                             PowerShell reference, where it is present

    Prerequisites are checked by name. "build failed" tells nobody what to
    install; "the .NET 10 SDK is not installed" does.

    There is no native toolchain to check for. The engine port is C#, so CMake,
    MSVC and the Windows SDK are not prerequisites of this repository.
    The one native library the app can use,
    BetterRuntimeCPU.dll, is built out of tree and copied in by a guarded item
    that simply does nothing when it is absent.
#>
[CmdletBinding()]
param(
    [switch]$Publish,
    [switch]$Parity,
    [string]$Configuration = 'Debug',
    [string]$Reference = $env:BT_PARITY_REFERENCE
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$solution = Join-Path $root 'BetterTranslator.sln'

function Fail([string]$message) {
    Write-Host "  FAILED  $message" -ForegroundColor Red
    exit 1
}

function Step([string]$message) {
    Write-Host ""
    Write-Host "== $message" -ForegroundColor Cyan
}

# --- prerequisites --------------------------------------------------------------

Step 'Checking prerequisites'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Fail 'The .NET SDK is not installed, or dotnet is not on PATH. Install the .NET 10 SDK from https://dotnet.microsoft.com/download'
}

$sdks = & dotnet --list-sdks
$ten = @($sdks | Where-Object { $_ -match '^10\.' })
if (-not $ten.Count) {
    Write-Host "  found:" -ForegroundColor Yellow
    $sdks | ForEach-Object { Write-Host "    $_" }
    Fail 'No .NET 10 SDK found. The solution targets net10.0 and net10.0-windows10.0.19041.0.'
}
Write-Host "  dotnet SDK: $($ten[0])" -ForegroundColor Green

if (-not (Test-Path $solution)) {
    Fail "The solution was not found at $solution"
}

# --- build ----------------------------------------------------------------------

Step 'Locating the Visual C++ runtime to carry beside the inference library'

$runtimeSupport = Join-Path $root 'artifacts\runtime-support'
$runtimeSupportModules = @('msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'vcomp140.dll')
$runtimeSupportProperty = @()
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'

if (Test-Path $vswhere) {
    $installation = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Redist.14.Latest -property installationPath
    if ($installation) {
        $redist = Get-ChildItem (Join-Path $installation 'VC\Redist\MSVC') -Directory -ErrorAction SilentlyContinue |
            Where-Object { Test-Path (Join-Path $_.FullName 'x64') } |
            Sort-Object Name -Descending |
            Select-Object -First 1
        if ($redist) {
            New-Item -ItemType Directory -Force $runtimeSupport | Out-Null
            foreach ($module in $runtimeSupportModules) {
                $found = Get-ChildItem (Join-Path $redist.FullName 'x64') -Recurse -Filter $module -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($found) { Copy-Item $found.FullName (Join-Path $runtimeSupport $module) -Force }
            }
        }
    }
}

$carried = $runtimeSupportModules | Where-Object { Test-Path (Join-Path $runtimeSupport $_) }
if ($carried.Count -eq $runtimeSupportModules.Count) {
    $runtimeSupportProperty = @("-p:RuntimeSupportDir=$runtimeSupport")
    Write-Host "  carrying $($carried.Count) modules from $runtimeSupport" -ForegroundColor Green
} else {
    Write-Host '  Visual C++ redistributable not found beside Visual Studio; the executable will rely on the system copy' -ForegroundColor Yellow
}

Step "Building ($Configuration)"
& dotnet build $solution -c $Configuration --nologo @runtimeSupportProperty
if ($LASTEXITCODE -ne 0) { Fail "dotnet build exited $LASTEXITCODE" }

# --- test -----------------------------------------------------------------------

Step 'Testing'
& dotnet test (Join-Path $root 'tests\BetterTranslator.Tests') -c $Configuration --nologo --no-build
if ($LASTEXITCODE -ne 0) { Fail "dotnet test exited $LASTEXITCODE" }

# --- parity ---------------------------------------------------------------------
#
# The fast parity tests always run: they compare the port against fixtures that
# are checked in. This switch adds the gated ones, which regenerate those
# fixtures from the reference scripts and fail if they have drifted -- so they
# need the reference repository present.

if ($Parity) {
    Step 'Parity against the PowerShell reference'

    if (-not $Reference) {
        Fail "The reference engine path is not set. Pass -Reference <path> or set BT_PARITY_REFERENCE, or drop -Parity to skip it."
    }

    if (-not (Test-Path $Reference)) {
        Fail "The reference engine was not found at $Reference. Pass -Reference <path>, or drop -Parity to skip it."
    }

    $env:BETTERTRANSLATOR_PARITY_TESTS = '1'
    try {
        & dotnet test (Join-Path $root 'tests\BetterTranslator.Tests') -c $Configuration --nologo --no-build --filter 'FullyQualifiedName~Parity'
        if ($LASTEXITCODE -ne 0) { Fail "parity tests exited $LASTEXITCODE" }
    }
    finally {
        Remove-Item Env:\BETTERTRANSLATOR_PARITY_TESTS -ErrorAction SilentlyContinue
    }
}

# --- publish --------------------------------------------------------------------

if ($Publish) {
    # The command line goes first: the app copies it into cli\ beside the
    # published exe, and the self-contained single file is the copy worth
    # shipping. Publishing it afterwards would ship the framework-dependent
    # build output instead, which needs a runtime the target machine may not
    # have.
    Step 'Publishing the command line'

    # The runtime identifier is passed on the command line rather than left to
    # the publish profile. A profile sets it as a project property, which does
    # not reach the referenced inference host, and a self-contained executable
    # may not reference one that is not -- NETSDK1150.
    & dotnet publish (Join-Path $root 'src\BetterTranslator.Cli') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishDir=bin/Release/net10.0/win-x64/publish/ --nologo
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited $LASTEXITCODE for the command line" }

    $bt = Join-Path $root 'src\BetterTranslator.Cli\bin\Release\net10.0\win-x64\publish\bt.exe'
    if (-not (Test-Path $bt)) { Fail "The publish reported success but $bt is not there" }

    Write-Host ("  {0:N0} bytes  {1}" -f (Get-Item $bt).Length, $bt) -ForegroundColor Green

    Step 'Publishing the distributable executable'

    & dotnet publish (Join-Path $root 'src\BetterTranslator.App') -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo @runtimeSupportProperty
    if ($LASTEXITCODE -ne 0) { Fail "dotnet publish exited $LASTEXITCODE" }

    $publish = Join-Path $root 'src\BetterTranslator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish'
    $exe = Join-Path $publish 'BetterTranslator.exe'
    if (-not (Test-Path $exe)) { Fail "The publish reported success but $exe is not there" }

    Write-Host ("  {0:N0} bytes  {1}" -f (Get-Item $exe).Length, $exe) -ForegroundColor Green

    # Settings > Agent renders a registration command pointing at this file.
    # Shipping without it hands the agent a path that is not there, and the
    # client reports nothing better than "connection closed".
    $beside = Join-Path $publish 'cli\bt.exe'
    if (-not (Test-Path $beside)) { Fail "The published app has no command line at $beside" }

    Write-Host ("  {0:N0} bytes  {1}" -f (Get-Item $beside).Length, $beside) -ForegroundColor Green
}

Write-Host ""
Write-Host "== Done" -ForegroundColor Green
