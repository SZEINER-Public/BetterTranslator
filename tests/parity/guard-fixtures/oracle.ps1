<#
    Captures the reference's own fixture corpus.

    scripts\test-guard.ps1 is a runner, not a data file: 844 lines of inline
    cases, every one "drawn from damage that actually happened or from the exact
    failure the gate was written to stop". That makes its INPUTS the most
    valuable thing in the reference repository, and they are not written down
    anywhere a port can read.

    So they are recorded instead of transcribed. Each guard function is wrapped
    with one that logs its arguments and its answer and then calls the original,
    and the fixture script is run with those wrappers in place. What comes out is
    every input the reference tests itself with, paired with what it decided.

    Read-only against ai-translation. The fixture script is read into memory and
    its two dot-source lines removed before it is executed, so the wrappers
    survive; nothing on disk there is touched.

    Regenerate with:
      powershell -NoProfile -File tests\parity\guard-fixtures\oracle.ps1
#>
param(
    [string]$Reference = $env:BT_PARITY_REFERENCE,
    [string]$Out = (Join-Path $PSScriptRoot 'oracle.json')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Reference) {
    throw 'Pass -Reference <path to the PowerShell reference engine>, or set BT_PARITY_REFERENCE.'
}

$scripts = Join-Path $Reference 'scripts'

. (Join-Path $scripts 'markup-guard.ps1')
if (Test-Path (Join-Path $scripts 'rag.ps1')) { . (Join-Path $scripts 'rag.ps1') }

$script:Calls = New-Object System.Collections.ArrayList

# Only the functions that have been ported. Recording the rest would produce a
# corpus for modules that cannot yet be compared against it.
$watch = @(
    'Protect-MarkupSafe', 'Restore-Markup', 'Get-SentinelIds', 'Get-PlaceholdersSafe',
    'Test-ChunkIntegrity', 'Remove-TranslatorNote', 'Test-ListingLine',
    'Get-SourceResidue', 'Test-SourceResidue', 'Get-TranslationDefect',
    'Test-OutputLeak', 'Test-DocumentStructure',
    # rag.ps1's slop validator, once it is dot-sourced above.
    'Invoke-SlopValidator', 'Repair-Symbols', 'Repair-Content', 'Get-RagPlaceholders',
    'Get-RagSystemSuffix', 'Get-DocumentTerms', 'Get-TermInconsistency',
    'Get-GlossaryHint', 'Test-Glossary'
)

<#
    Values reach JSON as strings so a wrapper never has to know the shape of what
    it is logging. An object with a Text property is one of Get-SourceResidue's
    runs; anything else is rendered plainly.
#>
function Flatten($value) {
    if ($null -eq $value) { return $null }
    if ($value -is [string]) { return $value }
    if ($value -is [bool] -or $value -is [int] -or $value -is [long] -or $value -is [double]) { return [string]$value }
    if ($value -is [System.Collections.IEnumerable]) {
        return @($value | ForEach-Object { Flatten $_ })
    }

    # Enumerated rather than asked for a Name collection: under Set-StrictMode,
    # reading .Name off an empty property set throws instead of yielding nothing,
    # and every scalar the fixtures pass has an empty one.
    $names = @()
    try { $names = @($value.PSObject.Properties | ForEach-Object { $_.Name }) } catch { }

    # Terminology rows. Rendered by their fields rather than falling through to
    # "@{Term=...; Count=...}", which a port cannot compare against.
    if ($names -contains 'Term') {
        if ($names -contains 'Kept') {
            return ('{0}|{1}|{2}|{3}' -f $value.Term, $value.Kept, $value.Translated, $value.Majority)
        }
        return ('{0}|{1}' -f $value.Term, $value.Count)
    }

    if ($names -contains 'Text') {
        $words = if ($names -contains 'Words') { [string]$value.Words } else { '' }
        $verdict = if ($names -contains 'Verdict') { [string]$value.Verdict } else { '' }
        return ('{0}|{1}|{2}' -f $value.Text, $words, $verdict)
    }

    return [string]$value
}

foreach ($name in $watch) {
    if (-not (Get-Command $name -CommandType Function -ErrorAction SilentlyContinue)) { continue }

    $original = (Get-Command $name -CommandType Function).ScriptBlock
    $captured = $name

    # No param block, so everything arrives in $args -- named parameters as a
    # -Name, value pair and positional ones on their own. Splatting @args back
    # re-passes both exactly as they came.
    $wrapper = {
        $result = & $original @args

        $pairs = New-Object System.Collections.ArrayList
        for ($i = 0; $i -lt $args.Count; $i++) {
            if ($args[$i] -is [string] -and $args[$i].StartsWith('-') -and ($i + 1) -lt $args.Count) {
                [void]$pairs.Add([pscustomobject]@{ name = $args[$i].TrimStart('-'); value = Flatten $args[$i + 1] })
                $i++
            }
            else {
                [void]$pairs.Add([pscustomobject]@{ name = "[$i]"; value = Flatten $args[$i] })
            }
        }

        [void]$script:Calls.Add([pscustomobject]@{
            fn     = $captured
            args   = @($pairs)
            result = Flatten $result
        })

        return $result
    }.GetNewClosure()

    Set-Item -Path ("function:script:" + $name) -Value $wrapper
}

# The fixture script, with its own dot-sources removed so the wrappers above are
# not overwritten by the originals.
$body = Get-Content (Join-Path $scripts 'test-guard.ps1') -Raw
$body = $body -replace '(?m)^\s*\.\s+"\$PSScriptRoot\\markup-guard\.ps1"\s*$', ''
$body = $body -replace '(?m)^\s*if \(\$ragAvailable\)\s*\{\s*\.\s+"\$PSScriptRoot\\rag\.ps1"\s*\}\s*$', ''
$body = $body -replace '(?m)^\s*exit\s+.*$', ''

# Shadowed rather than substituted. The fixture script uses $PSScriptRoot both
# inside quoted strings and bare as an argument; no single textual replacement is
# correct for both, and quoting the bare one broke Split-Path on the space in
# "Software Development". Assigning it at the top of the body works for every use
# because it is then just a variable.
$body = "`$PSScriptRoot = '$scripts'`r`n" + $body

# Its own Write-Host chatter is not wanted; only the recorded calls are.
& ([scriptblock]::Create($body)) *> $null

$doc = [pscustomobject]@{
    generatedBy = 'tests\parity\guard-fixtures\oracle.ps1'
    reference   = 'ai-translation\scripts\test-guard.ps1'
    calls       = @($script:Calls)
}

$json = $doc | ConvertTo-Json -Depth 12
[IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))

$byFn = $script:Calls | Group-Object fn | Sort-Object Name
Write-Output ("wrote {0}: {1} recorded calls" -f $Out, $script:Calls.Count)
foreach ($g in $byFn) { Write-Output ("  {0,-24} {1}" -f $g.Name, $g.Count) }
