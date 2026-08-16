[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Artifact,
    [string]$ProbeArtifact,
    [string]$RepackedArtifact,
    [string]$ReportPath,
    [int]$WarmStartSamples = 7,
    [int]$StartTimeoutSeconds = 180
)

$ErrorActionPreference = 'Stop'

$script:Desktop = 'BetterTranslatorChecks'
$script:CacheRoot = Join-Path $env:LOCALAPPDATA 'SZEINER\BetterTranslator'
$script:LogPath = Join-Path $script:CacheRoot 'logs\bootstrap.log'
$script:Results = [System.Collections.Generic.List[object]]::new()
$script:WorkRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("bt-checks-" + [guid]::NewGuid().ToString('n'))

if (-not $ReportPath) {
    $repositoryRoot = Split-Path $Artifact -Parent
    while ($repositoryRoot -and -not (Test-Path (Join-Path $repositoryRoot 'BetterTranslator.sln'))) {
        $repositoryRoot = Split-Path $repositoryRoot -Parent
    }

    $ReportPath = if ($repositoryRoot) {
        Join-Path $repositoryRoot 'obj\single-file\checks-report.md'
    }
    else {
        Join-Path (Split-Path $Artifact -Parent) 'checks-report.md'
    }
}

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

public static class DesktopLauncher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    private const int StartUseStdHandles = 0x00000100;
    private const int StartUseShowWindow = 0x00000001;
    private const short ShowHide = 0;
    private const int CreateNoWindow = 0x08000000;
    private const int CreateNewConsole = 0x00000010;
    private const int CreateUnicodeEnvironment = 0x00000400;
    private const int GenericWrite = 0x40000000;
    private const int FileShareRead = 0x00000001;
    private const int FileShareWrite = 0x00000002;
    private const int CreateAlways = 2;
    private const int StillActive = 259;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string desktop, string device, IntPtr devmode, int flags,
                                                int access, IntPtr attributes);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktopW(string desktop, int flags, bool inherit, int access);

    private delegate bool EnumWindowsProc(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, EnumWindowsProc callback, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr window, System.Text.StringBuilder name, int capacity);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, int access, int share, ref SecurityAttributes security,
                                             int disposition, int flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(string application, string commandLine, IntPtr processAttributes,
                                              IntPtr threadAttributes, bool inheritHandles, int flags,
                                              IntPtr environment, string workingDirectory,
                                              ref StartupInfo startupInfo, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, int exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IntPtr CreateCheckDesktop(string name)
    {
        IntPtr desktop = CreateDesktopW(name, null, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");
        }
        return desktop;
    }

    public static void DestroyCheckDesktop(IntPtr desktop)
    {
        if (desktop != IntPtr.Zero)
        {
            CloseDesktop(desktop);
        }
    }

    public static string[] WindowClassesOn(string desktopName)
    {
        IntPtr desktop = OpenDesktopW(desktopName, 0, false, 0x0100);
        if (desktop == IntPtr.Zero)
        {
            return new string[0];
        }

        var classes = new System.Collections.Generic.List<string>();
        EnumDesktopWindows(desktop, delegate (IntPtr window, IntPtr parameter)
        {
            var name = new System.Text.StringBuilder(256);
            if (GetClassNameW(window, name, name.Capacity) > 0)
            {
                classes.Add(name.ToString());
            }
            return true;
        }, IntPtr.Zero);

        CloseDesktop(desktop);
        return classes.ToArray();
    }

    public static IntPtr LaunchInOwnConsole(string application, string commandLine, string desktopName)
    {
        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        startup.lpDesktop = desktopName;
        startup.dwFlags = StartUseShowWindow;
        startup.wShowWindow = ShowHide;

        ProcessInformation information;
        string line = "\"" + application + "\"" + (string.IsNullOrEmpty(commandLine) ? "" : " " + commandLine);

        if (!CreateProcessW(application, line, IntPtr.Zero, IntPtr.Zero, false,
                            CreateNewConsole | CreateUnicodeEnvironment, IntPtr.Zero, null,
                            ref startup, out information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for " + application);
        }

        CloseHandle(information.hThread);
        return information.hProcess;
    }

    public static IntPtr Launch(string application, string commandLine, string desktopName,
                                string standardOutput, string standardError, bool withConsoleHandles)
    {
        var security = new SecurityAttributes();
        security.Length = Marshal.SizeOf(typeof(SecurityAttributes));
        security.InheritHandle = 1;

        IntPtr outHandle = IntPtr.Zero;
        IntPtr errHandle = IntPtr.Zero;

        var startup = new StartupInfo();
        startup.cb = Marshal.SizeOf(typeof(StartupInfo));
        startup.lpDesktop = desktopName;
        startup.dwFlags = StartUseShowWindow;
        startup.wShowWindow = ShowHide;

        if (withConsoleHandles)
        {
            outHandle = CreateFileW(standardOutput, GenericWrite, FileShareRead | FileShareWrite,
                                    ref security, CreateAlways, 0, IntPtr.Zero);
            errHandle = CreateFileW(standardError, GenericWrite, FileShareRead | FileShareWrite,
                                    ref security, CreateAlways, 0, IntPtr.Zero);

            if (outHandle == new IntPtr(-1) || errHandle == new IntPtr(-1))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateFile for redirection failed");
            }

            startup.dwFlags |= StartUseStdHandles;
            startup.hStdOutput = outHandle;
            startup.hStdError = errHandle;
            startup.hStdInput = IntPtr.Zero;
        }

        ProcessInformation information;
        string line = "\"" + application + "\"" + (string.IsNullOrEmpty(commandLine) ? "" : " " + commandLine);

        if (!CreateProcessW(application, line, IntPtr.Zero, IntPtr.Zero, withConsoleHandles,
                            CreateNoWindow | CreateUnicodeEnvironment, IntPtr.Zero, null,
                            ref startup, out information))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for " + application);
        }

        CloseHandle(information.hThread);

        if (outHandle != IntPtr.Zero) { CloseHandle(outHandle); }
        if (errHandle != IntPtr.Zero) { CloseHandle(errHandle); }

        return information.hProcess;
    }

    public static bool Running(IntPtr process)
    {
        int code;
        return GetExitCodeProcess(process, out code) && code == StillActive;
    }

    public static int Wait(IntPtr process, int milliseconds)
    {
        WaitForSingleObject(process, milliseconds);
        int code;
        GetExitCodeProcess(process, out code);
        return code;
    }

    public static void Kill(IntPtr process)
    {
        if (Running(process))
        {
            TerminateProcess(process, 0);
            WaitForSingleObject(process, 10000);
        }
        CloseHandle(process);
    }

    public static void Release(IntPtr process)
    {
        CloseHandle(process);
    }
}
'@ -Language CSharp

function New-WorkPath([string]$name) {
    $path = Join-Path $script:WorkRoot $name
    New-Item -ItemType Directory -Force -Path (Split-Path $path -Parent) | Out-Null
    return $path
}

function Invoke-Artifact([string]$exe, [string]$arguments, [int]$timeoutSeconds = 120) {
    $out = New-WorkPath ("run-" + [guid]::NewGuid().ToString('n') + ".out")
    $err = "$out.err"

    $process = [DesktopLauncher]::Launch($exe, $arguments, $script:Desktop, $out, $err, $true)
    $code = [DesktopLauncher]::Wait($process, $timeoutSeconds * 1000)
    [DesktopLauncher]::Release($process)

    $text = ''
    foreach ($file in @($out, $err)) {
        if (Test-Path $file) { $text += (Get-Content $file -Raw -ErrorAction SilentlyContinue) }
    }

    return [pscustomobject]@{ ExitCode = $code; Output = $text }
}

function Start-Artifact([string]$exe, [string]$arguments, [bool]$capture = $false) {
    $out = New-WorkPath ("bg-" + [guid]::NewGuid().ToString('n') + ".out")
    return [DesktopLauncher]::Launch($exe, $arguments, $script:Desktop, $out, "$out.err", $capture)
}

function Wait-ForLogLine([string]$pattern, [int]$timeoutSeconds, [int]$sinceLine) {
    $deadline = (Get-Date).AddSeconds($timeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        if (Test-Path $script:LogPath) {
            $lines = @(Get-Content $script:LogPath -ErrorAction SilentlyContinue)
            for ($index = $sinceLine; $index -lt $lines.Count; $index++) {
                if ($lines[$index] -match $pattern) { return $lines[$index] }
            }
        }
        Start-Sleep -Milliseconds 100
    }
    return $null
}

function Get-LogLength() {
    if (-not (Test-Path $script:LogPath)) { return 0 }
    return @(Get-Content $script:LogPath -ErrorAction SilentlyContinue).Count
}

function Add-Result([string]$name, [bool]$passed, [string]$detail) {
    $script:Results.Add([pscustomobject]@{ Check = $name; Passed = $passed; Detail = $detail })
    $mark = if ($passed) { 'PASS' } else { 'FAIL' }
    $colour = if ($passed) { 'Green' } else { 'Red' }
    Write-Host ("  {0}  {1}" -f $mark, $name) -ForegroundColor $colour
    if ($detail) { Write-Host "        $detail" }
}

function Clear-RuntimeCache() {
    $runtime = Join-Path $script:CacheRoot 'runtime'
    if (Test-Path $runtime) {
        Get-ChildItem $runtime -Recurse -Force -ErrorAction SilentlyContinue |
            ForEach-Object { $_.Attributes = 'Normal' }
        Remove-Item $runtime -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Get-RuntimeDirectories() {
    $runtime = Join-Path $script:CacheRoot 'runtime'
    if (-not (Test-Path $runtime)) { return @() }
    return @(Get-ChildItem $runtime -Directory -Force -ErrorAction SilentlyContinue)
}

New-Item -ItemType Directory -Force -Path $script:WorkRoot | Out-Null
$desktopHandle = [DesktopLauncher]::CreateCheckDesktop($script:Desktop)
Write-Host "== Single file checks on desktop $script:Desktop" -ForegroundColor Cyan

try {
    Write-Host ""
    Write-Host "-- payload and self test" -ForegroundColor Cyan

    $selfTest = Invoke-Artifact $Artifact '--bt-selftest' 300
    Add-Result 'selftest' ($selfTest.ExitCode -eq 0 -and $selfTest.Output -match 'selftest PASS') `
        (($selfTest.Output -split "`r?`n" | Where-Object { $_ -match 'entry expansion|container hash\s+ok|selftest' }) -join ' | ')

    $release = @(Get-ChildItem (Split-Path $Artifact -Parent) -Force)
    Add-Result 'release directory holds exactly one file' ($release.Count -eq 1) `
        ("{0} entries, {1:N0} bytes" -f $release.Count, (Get-Item $Artifact).Length)

    if ($RepackedArtifact -and (Test-Path $RepackedArtifact)) {
        $first = (Get-FileHash $Artifact -Algorithm SHA256).Hash
        $second = (Get-FileHash $RepackedArtifact -Algorithm SHA256).Hash
        Add-Result 'two packer runs on the same input are byte identical' ($first -eq $second) `
            ("$first vs $second")
    }

    Write-Host ""
    Write-Host "-- cold start" -ForegroundColor Cyan

    Clear-RuntimeCache
    $mark = Get-LogLength
    $coldStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $cold = Start-Artifact $Artifact ''
    $coldLine = Wait-ForLogLine 'cold start, bootstrap overhead' $StartTimeoutSeconds $mark
    $coldStopwatch.Stop()
    $extractLine = Wait-ForLogLine 'extracted \d+ entries' 5 $mark

    $windowSeen = $false
    $deadline = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $deadline -and -not $windowSeen) {
        $windowSeen = @([DesktopLauncher]::WindowClassesOn($script:Desktop)) -match 'HwndWrapper\[BetterTranslator'
        if (-not $windowSeen) { Start-Sleep -Milliseconds 200 }
    }

    $classesWhileRunning = @([DesktopLauncher]::WindowClassesOn($script:Desktop))
    $consoleSeen = $classesWhileRunning -match 'ConsoleWindowClass'
    $runtimeDirectories = Get-RuntimeDirectories
    $staging = @($runtimeDirectories | Where-Object { $_.Name -like '*.staging-*' })

    [DesktopLauncher]::Kill($cold)

    Add-Result 'cold start reaches the application' ($null -ne $coldLine -and $windowSeen) `
        ("wall clock {0:N0} ms | {1}" -f $coldStopwatch.ElapsedMilliseconds, $extractLine)
    Add-Result 'cold start leaves no staging directory' ($staging.Count -eq 0) `
        ("runtime directories: " + (($runtimeDirectories | ForEach-Object { $_.Name }) -join ', '))
    Add-Result 'launch without a console creates no console window' (-not $consoleSeen) `
        ("window classes while running: " + ($classesWhileRunning -join ', '))

    Write-Host ""
    Write-Host "-- warm start" -ForegroundColor Cyan

    $overheads = @()
    for ($sample = 0; $sample -lt $WarmStartSamples; $sample++) {
        $mark = Get-LogLength
        $warm = Start-Artifact $Artifact ''
        $warmLine = Wait-ForLogLine 'warm start, bootstrap overhead' 60 $mark
        [DesktopLauncher]::Kill($warm)

        if ($warmLine -match 'bootstrap overhead ([0-9.]+) ms') {
            $overheads += [double]$Matches[1]
        }
    }

    $sorted = @($overheads | Sort-Object)
    $median = if ($sorted.Count -gt 0) { $sorted[[int]($sorted.Count / 2)] } else { [double]::NaN }
    $worst = if ($sorted.Count -gt 0) { $sorted[-1] } else { [double]::NaN }

    Add-Result 'warm start overhead stays under 50 ms' ($sorted.Count -gt 0 -and $worst -lt 50) `
        ("n={0} min={1:N3} ms median={2:N3} ms max={3:N3} ms" -f $sorted.Count, $sorted[0], $median, $worst)

    Write-Host ""
    Write-Host "-- concurrency" -ForegroundColor Cyan

    Clear-RuntimeCache
    $mark = Get-LogLength
    $first = Start-Artifact $Artifact ''
    $second = Start-Artifact $Artifact ''

    $bothStarted = $null -ne (Wait-ForLogLine 'cold start, bootstrap overhead' $StartTimeoutSeconds $mark)
    $deadline = (Get-Date).AddSeconds(60)
    $startLines = 0
    while ((Get-Date) -lt $deadline) {
        $lines = @(Get-Content $script:LogPath -ErrorAction SilentlyContinue)
        $startLines = @($lines[$mark..($lines.Count - 1)] | Where-Object { $_ -match 'start, bootstrap overhead' }).Count
        if ($startLines -ge 2) { break }
        Start-Sleep -Milliseconds 200
    }

    $lines = @(Get-Content $script:LogPath -ErrorAction SilentlyContinue)
    $window = @($lines[$mark..($lines.Count - 1)])
    $extractions = @($window | Where-Object { $_ -match 'extracted \d+ entries' }).Count
    $waited = @($window | Where-Object { $_ -match 'while this one waited|promoted the runtime first' }).Count

    [DesktopLauncher]::Kill($first)
    [DesktopLauncher]::Kill($second)

    Add-Result 'two simultaneous cold starts both reach the application' ($bothStarted -and $startLines -ge 2) `
        ("start lines: $startLines")
    Add-Result 'exactly one extraction is performed' ($extractions -eq 1) `
        ("extractions: $extractions, waiters that reused it: $waited")

    Write-Host ""
    Write-Host "-- corrupted payload" -ForegroundColor Cyan

    $corrupted = New-WorkPath 'corrupted\BetterTranslator.exe'
    Copy-Item $Artifact $corrupted -Force
    $bytes = [System.IO.File]::ReadAllBytes($corrupted)
    $bytes[$bytes.Length - 4096] = $bytes[$bytes.Length - 4096] -bxor 0xFF
    [System.IO.File]::WriteAllBytes($corrupted, $bytes)

    $corruptCache = New-WorkPath 'corrupt-cache'
    New-Item -ItemType Directory -Force -Path $corruptCache | Out-Null

    $corruptRun = Invoke-Artifact $corrupted "--bt-cache-path `"$corruptCache`"" 300
    $corruptLog = Join-Path $corruptCache 'logs\bootstrap.log'
    $corruptLogged = (Test-Path $corruptLog) -and
                     ((Get-Content $corruptLog -Raw) -match 'fatal 11|hash is')
    $promoted = @(Get-ChildItem (Join-Path $corruptCache 'runtime') -Directory -Force -ErrorAction SilentlyContinue)

    Add-Result 'a corrupted payload exits 11, logs it and promotes nothing' `
        ($corruptRun.ExitCode -eq 11 -and $promoted.Count -eq 0 -and $corruptLogged) `
        ("exit {0}, promoted {1} directories, log entry {2}" -f $corruptRun.ExitCode, $promoted.Count, $corruptLogged)

    Write-Host ""
    Write-Host "-- console output" -ForegroundColor Cyan

    $consoleRun = Invoke-Artifact $Artifact '--bt-verify' 300
    Add-Result 'a launch with redirected output writes to the redirection' `
        ($consoleRun.ExitCode -eq 0 -and $consoleRun.Output -match 'verify OK') `
        (($consoleRun.Output -split "`r?`n" | Select-Object -First 3) -join ' | ')

    $capture = New-WorkPath 'console-capture.txt'
    $captureError = "$capture.err"
    $harness = New-WorkPath 'console-harness.ps1'

    Set-Content -Path $harness -Encoding utf8 -Value @"
`$ErrorActionPreference = 'Stop'
try {
    `$run = Start-Process -FilePath '$Artifact' -ArgumentList '--bt-verify' -NoNewWindow -Wait -PassThru
    `$ui = `$Host.UI.RawUI
    `$bottom = [Math]::Max(0, `$ui.CursorPosition.Y - 1)
    `$rect = New-Object System.Management.Automation.Host.Rectangle 0, 0, (`$ui.BufferSize.Width - 1), `$bottom
    `$cells = `$ui.GetBufferContents(`$rect)
    `$text = New-Object System.Text.StringBuilder
    for (`$y = 0; `$y -le `$bottom; `$y++) {
        `$line = New-Object System.Text.StringBuilder
        for (`$x = 0; `$x -lt `$ui.BufferSize.Width; `$x++) {
            [void]`$line.Append(`$cells.GetValue(`$y, `$x).Character)
        }
        [void]`$text.AppendLine(`$line.ToString().TrimEnd())
    }
    Set-Content -Path '$capture' -Value `$text.ToString() -Encoding utf8
    exit `$run.ExitCode
}
catch {
    Set-Content -Path '$captureError' -Value (`$_ | Out-String) -Encoding utf8
    exit 99
}
"@

    $consoleHarness = [DesktopLauncher]::LaunchInOwnConsole(
        (Get-Command powershell.exe).Source,
        "-NoProfile -ExecutionPolicy Bypass -File `"$harness`"",
        $script:Desktop)
    $harnessExit = [DesktopLauncher]::Wait($consoleHarness, 300000)
    [DesktopLauncher]::Release($consoleHarness)

    $captured = if (Test-Path $capture) { Get-Content $capture -Raw } else { '' }
    $captureFailure = if (Test-Path $captureError) { (Get-Content $captureError -Raw) -replace '\s+', ' ' } else { '' }

    Add-Result 'a launch from an existing console prints into that console' `
        ($harnessExit -eq 0 -and $captured -match 'verify OK') `
        ("harness exit {0}, {1} matching lines read back out of the console buffer{2}" -f $harnessExit,
            @($captured -split "`r?`n" | Where-Object { $_ -match 'container hash|verify OK' }).Count,
            $(if ($captureFailure) { ", harness error: $captureFailure" } else { '' }))

    if ($ProbeArtifact -and (Test-Path $ProbeArtifact)) {
        Write-Host ""
        Write-Host "-- argument and exit code forwarding" -ForegroundColor Cyan

        $cases = @(
            @{ Name = 'no arguments'; Arguments = ''; Expect = 0; Contains = @('argc=0') }
            @{ Name = 'plain arguments and exit code'; Arguments = 'alpha beta --exit=42'; Expect = 42
               Contains = @('argc=3', 'arg=[alpha]', 'arg=[beta]', 'arg=[--exit=42]') }
            @{ Name = 'quoted arguments with spaces'; Arguments = '"first argument" "C:\path with spaces\file.txt" --exit=7'
               Expect = 7; Contains = @('argc=3', 'arg=[first argument]', 'arg=[C:\path with spaces\file.txt]') }
            @{ Name = 'bootstrap flags are consumed and never forwarded'
               Arguments = '--bt-verify-not-a-flag'; Expect = 17; Contains = @() }
            @{ Name = 'cache path flag is stripped from the forwarded arguments'
               Arguments = "--bt-cache-path `"$(New-WorkPath 'probe-cache')`" kept `"also kept`" --exit=3"
               Expect = 3; Contains = @('argc=3', 'arg=[kept]', 'arg=[also kept]') }
        )

        foreach ($case in $cases) {
            $run = Invoke-Artifact $ProbeArtifact $case.Arguments 300
            $missing = @($case.Contains | Where-Object { $run.Output -notmatch [regex]::Escape($_) })
            Add-Result ("forwarding: " + $case.Name) `
                ($run.ExitCode -eq $case.Expect -and $missing.Count -eq 0) `
                ("exit {0} expected {1}{2}" -f $run.ExitCode, $case.Expect,
                    $(if ($missing.Count) { ", missing " + ($missing -join ', ') } else { '' }))
        }
    }
    else {
        Add-Result 'argument and exit code forwarding' $false 'no probe artifact was supplied'
    }

    Write-Host ""
    Write-Host "-- final desktop state" -ForegroundColor Cyan
    $remaining = @([DesktopLauncher]::WindowClassesOn($script:Desktop))
    Add-Result 'no console window was created on the check desktop' `
        (-not ($remaining -match 'ConsoleWindowClass')) ("classes: " + ($remaining -join ', '))
}
finally {
    [DesktopLauncher]::DestroyCheckDesktop($desktopHandle)
}

$failed = @($script:Results | Where-Object { -not $_.Passed })

$report = [System.Text.StringBuilder]::new()
[void]$report.AppendLine('# Single file checks')
[void]$report.AppendLine()
[void]$report.AppendLine("Artifact: ``$Artifact``")
[void]$report.AppendLine("Bytes: $((Get-Item $Artifact).Length)")
[void]$report.AppendLine("Machine: $env:COMPUTERNAME, $([Environment]::OSVersion.VersionString), $([Environment]::ProcessorCount) logical processors")
[void]$report.AppendLine("Desktop: $script:Desktop, created with CreateDesktop and destroyed at the end of the run")
[void]$report.AppendLine()
[void]$report.AppendLine('| Check | Result | Detail |')
[void]$report.AppendLine('|---|---|---|')

foreach ($result in $script:Results) {
    $detail = ($result.Detail -replace '\|', '\|')
    [void]$report.AppendLine("| $($result.Check) | $(if ($result.Passed) { 'PASS' } else { 'FAIL' }) | $detail |")
}

[void]$report.AppendLine()
[void]$report.AppendLine("Passed $($script:Results.Count - $failed.Count) of $($script:Results.Count).")

New-Item -ItemType Directory -Force -Path (Split-Path $ReportPath -Parent) | Out-Null
Set-Content -Path $ReportPath -Value $report.ToString() -Encoding utf8

Remove-Item $script:WorkRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Host "== Report written to $ReportPath" -ForegroundColor Green

if ($failed.Count -gt 0) {
    Write-Host "== $($failed.Count) check(s) failed" -ForegroundColor Red
    exit 1
}

Write-Host "== All checks passed" -ForegroundColor Green
