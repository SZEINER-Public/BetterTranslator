<#
    Dumps every answer scripts\languages.ps1 gives, as JSON, so the C# port can
    be compared against the reference implementation rather than against what
    someone remembers it doing.

    Read-only. It dot-sources the reference and calls it; nothing under
    ai-translation is written, and the file it reads is the registry, not a
    payload.

    Regenerate with:
      powershell -NoProfile -File tests\parity\languages\oracle.ps1

    The output is checked in as oracle.json so the default test suite can
    compare without paying for a PowerShell launch. A gated test re-runs this
    and fails if the reference has moved underneath the fixture.
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

. (Join-Path $Reference 'scripts\languages.ps1')

# Seeded explicitly: $PSScriptRoot inside a dot-sourced function resolves
# against the caller, and the point here is the logic, not path discovery.
$null = Import-Languages -Path (Join-Path $Reference 'config\languages.json')
$all = @(Import-Languages)

# Every query shape Resolve-Language promises to accept, plus the ones it must
# refuse. Built from the registry itself so it covers all 51 rows rather than a
# handful someone chose.
$queries = New-Object System.Collections.ArrayList
foreach ($l in $all) {
    [void]$queries.Add($l.code)
    [void]$queries.Add($l.code.ToUpperInvariant())
    [void]$queries.Add($l.name)
    [void]$queries.Add($l.name.ToLowerInvariant())
    [void]$queries.Add($l.native)
    if ($l.PSObject.Properties.Name -contains 'alias') {
        foreach ($a in @($l.alias)) { [void]$queries.Add($a) }
    }
}
foreach ($q in @('Norweg', 'Czec', 'Ch', 'C', '', '   ', 'Klingon', 'zz', 'ua', 'UA', ' cs ')) {
    [void]$queries.Add($q)
}

$resolved = New-Object System.Collections.ArrayList
foreach ($q in ($queries | Select-Object -Unique)) {
    $r = Resolve-Language -Name $q
    [void]$resolved.Add([pscustomobject]@{
        query = $q
        code  = $(if ($r) { [string]$r.code } else { $null })
    })
}

$patterns = New-Object System.Collections.ArrayList
foreach ($l in $all) {
    [void]$patterns.Add([pscustomobject]@{
        code    = [string]$l.code
        native  = [string]$l.native
        pattern = [string](Get-ScriptPattern -Language $l)
    })
}

$models = New-Object System.Collections.ArrayList
foreach ($m in @('', 'eurollm', 'EuroLLM-9B-Instruct-Q4_K_M', 'translategemma-4b-it.Q4_K_M', 'gemma3-1b', 'some-unknown-model')) {
    [void]$models.Add([pscustomobject]@{
        modelId  = $m
        flag     = [string](Get-ModelFlag -ModelId $m)
        verified = [bool](Test-LanguageVerified -ModelId $m)
        codes    = @(Get-LanguagesForModel -ModelId $m | ForEach-Object { [string]$_.code })
    })
}

$warnings = New-Object System.Collections.ArrayList
foreach ($code in @('cs', 'th', 'ja', 'de', 'uk')) {
    $l = Resolve-Language -Name $code
    foreach ($m in @('', 'eurollm', 'translategemma-4b-it')) {
        [void]$warnings.Add([pscustomobject]@{
            code    = $code
            modelId = $m
            warning = [string](Get-LanguageWarning -Language $l -ModelId $m)
        })
    }
}

# --- model prompts -------------------------------------------------------------
#
# The shape a model was trained on. Only listed models have one; everything else
# gets $null, which is the documented answer and not a failure.

$prompts = New-Object System.Collections.ArrayList
foreach ($m in @('', 'translategemma-4b-it.Q4_K_M', 'TRANSLATEGEMMA', 'eurollm', 'EuroLLM-9B-Instruct-Q4_K_M', 'gemma3-1b', 'unknown-model')) {
    foreach ($t in @(@{ Name = 'Czech'; Code = 'cs' }, @{ Name = 'German'; Code = 'de' })) {
        $p = Get-ModelPrompt -ModelId $m -SourceLanguage 'English' -SourceCode 'en' -TargetLanguage $t.Name -TargetCode $t.Code
        [void]$prompts.Add([pscustomobject]@{
            modelId     = $m
            target      = $t.Name
            hasShape    = [bool]$p
            label       = $(if ($p) { [string]$p.Label } else { $null })
            role        = $(if ($p) { [string]$p.Role } else { $null })
            blankLines  = $(if ($p) { [int]$p.BlankLines } else { $null })
            instruction = $(if ($p) { [string]$p.Instruction } else { $null })
        })
    }
}

# --- the engine's own system prompt ---------------------------------------------
#
# Get-TranslationSystemPrompt lives in console.ps1, which cannot be dot-sourced:
# the file is an interactive session and running it would start one. The function
# is lifted out of the parse tree instead, which defines it without executing a
# line of the surrounding script.

$consolePath = Join-Path $Reference 'scripts\console.ps1'
$consoleAst = [System.Management.Automation.Language.Parser]::ParseFile($consolePath, [ref]$null, [ref]$null)

$fnAst = $consoleAst.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-TranslationSystemPrompt'
}, $true) | Select-Object -First 1

if (-not $fnAst) { throw 'Get-TranslationSystemPrompt was not found in console.ps1' }

# Defines the function in this scope. -Rag $null keeps it to the base prompt,
# which is the part that does not depend on a rules file being present.
. ([scriptblock]::Create($fnAst.Extent.Text))

$systemPrompts = New-Object System.Collections.ArrayList
foreach ($lang in @('Czech', 'German', 'Japanese')) {
    [void]$systemPrompts.Add([pscustomobject]@{
        language = $lang
        prompt   = [string](Get-TranslationSystemPrompt -Language $lang -Rag $null)
    })
}

$doc = [pscustomobject]@{
    generatedBy = 'tests\parity\languages\oracle.ps1'
    systemPrompts = @($systemPrompts)
    reference   = 'ai-translation\scripts\languages.ps1'
    total       = $all.Count
    resolve     = @($resolved)
    patterns    = @($patterns)
    models      = @($models)
    warnings    = @($warnings)
    prompts     = @($prompts)
}

# UTF-8 with no BOM. Writing one would reintroduce the very defect the engine
# strips on read.
$json = $doc | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))

Write-Output ("wrote {0} ({1} languages, {2} resolve cases)" -f $Out, $all.Count, $resolved.Count)
