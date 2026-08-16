<#
    Dumps what scripts\markup-guard.ps1 answers for a fixture corpus, so the C#
    port can be compared against the reference rather than against a reading of
    it.

    Read-only against ai-translation: dot-sources and calls, never writes.

    The fixtures are built from the defects the script's own header names -- a
    dropped fence, a truncated chunk, a translated flag, an invented URL, a
    flipped bracket, a lost heading marker, a manufactured placeholder from
    ::WriteAllText, an inflected do-not-translate term. Synthetic text
    throughout; no payload or locale content goes near this.

    Regenerate with:
      powershell -NoProfile -File tests\parity\markup\oracle.ps1
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

. (Join-Path $Reference 'scripts\markup-guard.ps1')

$nl = "`n"

# --- protect / restore --------------------------------------------------------

$protectCases = @(
    'plain prose with nothing to protect',
    ('before' + $nl + '```powershell' + $nl + 'Get-ChildItem -Path C:\Temp' + $nl + '```' + $nl + 'after'),
    ('one ```js' + $nl + 'const a = 1;' + $nl + '``` two ```sh' + $nl + 'ls -la' + $nl + '``` three'),
    'see <!-- a hidden note --> here',
    'multi <!-- one --> and <!-- two --> comments',
    'read https://example.com/docs?a=1&b=2 for more',
    'link (https://example.com/x) in brackets',
    'open C:\Program\Files\thing.txt now',
    'path D:\a\b and url https://example.org/y together',
    ('# Heading' + $nl + $nl + '```' + $nl + 'code' + $nl + '```' + $nl + $nl + 'See https://example.com and C:\tmp\f.log'),
    'no protection but [[0]] looks like a sentinel',
    ''
)

$protect = New-Object System.Collections.ArrayList
foreach ($c in $protectCases) {
    if ([string]::IsNullOrEmpty($c)) {
        # Protect-Markup declares Text mandatory and rejects an empty string, so
        # the empty case is recorded as the reference's refusal rather than run.
        [void]$protect.Add([pscustomobject]@{
            source = $c; text = $null; store = @(); restored = $null; unsupported = $true
        })
        continue
    }
    $p = Protect-MarkupSafe -Text $c
    $store = @($p.Store)
    [void]$protect.Add([pscustomobject]@{
        source      = $c
        text        = [string]$p.Text
        store       = @($store | ForEach-Object { [string]$_ })
        restored    = [string](Restore-Markup -Text $p.Text -Store $store)
        unsupported = $false
    })
}

# --- sentinel ids -------------------------------------------------------------

$sentinelCases = @(
    '', 'none here', '[[0]]', '[[0]] and [[1]]', '[[10]] then [[2]] then [[1]]',
    '[[3]] [[3]] repeated', 'text [[12]] more [[0]] end', '[[a]] not an id', '[[]] empty'
)
$sentinels = New-Object System.Collections.ArrayList
foreach ($c in $sentinelCases) {
    [void]$sentinels.Add([pscustomobject]@{ text = $c; ids = @(Get-SentinelIds -Text $c) })
}

# --- placeholders -------------------------------------------------------------

$placeholderCases = @(
    '',
    'Welcome :user to the app',
    'Hello {{ name }} and {{other}}',
    'Value {0} then {1} then {10}',
    'Config {app.name} and {app_id}',
    'Printf %s and %d and %f and %x',
    'Mixed {{a}} {0} :user %s {b.c}',
    'Not a placeholder: [System.IO.File]::WriteAllText',
    'Not a placeholder: -Confirm:$false',
    'Edge ::double and $:dollar and word:after',
    'Time is 12:30 and ratio 1:2',
    'Repeated {0} {0} {0}',
    'Braces {} and {  } and {a b}'
)
$placeholders = New-Object System.Collections.ArrayList
foreach ($c in $placeholderCases) {
    [void]$placeholders.Add([pscustomobject]@{ text = $c; found = @(Get-PlaceholdersSafe -Text $c) })
}

# --- chunk integrity ----------------------------------------------------------

$long = ('The quick brown fox jumps over the lazy dog and keeps running for a while. ' * 4)

$integrityCases = @(
    @{ n = 'clean single line';     s = 'Hello world';                     t = 'Ahoj svete' }
    @{ n = 'empty answer';          s = 'Hello world';                     t = '' }
    @{ n = 'whitespace answer';     s = 'Hello world';                     t = '   ' }
    @{ n = 'sentinel kept';         s = 'See [[0]] here';                  t = 'Viz [[0]] zde' }
    @{ n = 'sentinel dropped';      s = 'See [[0]] here';                  t = 'Viz zde' }
    @{ n = 'sentinel invented';     s = 'See [[0]] here';                  t = 'Viz [[0]] a [[1]] zde' }
    @{ n = 'sentinel renumbered';   s = 'A [[0]] B [[1]]';                 t = 'A [[2]] B [[3]]' }
    @{ n = 'placeholder kept';      s = 'Welcome :user';                   t = 'Vitej :user' }
    @{ n = 'placeholder lost';      s = 'Welcome :user';                   t = 'Vitej uzivateli' }
    @{ n = 'placeholder altered';   s = 'Value {0}';                       t = 'Hodnota {1}' }
    @{ n = 'backtick lost';         s = 'Run `build` now';                 t = 'Spustte build nyni' }
    @{ n = 'backtick balanced';     s = 'Run `build` now';                 t = 'Spustte `build` nyni' }
    @{ n = 'option translated';     s = 'Use --no-serve here';             t = 'Pouzijte --ne-serve zde' }
    @{ n = 'option kept';           s = 'Use --no-serve here';             t = 'Pouzijte --no-serve zde' }
    @{ n = 'option count changed';  s = 'Use --a-flag and --b-flag';       t = 'Pouzijte --a-flag' }
    @{ n = 'url invented';          s = 'A choice of things';              t = 'Vyber veci https://www.cnet.com/news/' }
    @{ n = 'url kept';              s = 'Go to https://x.test now';        t = 'Prejdete na https://x.test nyni' }
    @{ n = 'bracket flipped';       s = 'Through the proxy (x)';           t = 'Pres proxy )x(' }
    @{ n = 'bracket source open';   s = 'Through the proxy (';             t = 'Pres proxy )' }
    @{ n = 'heading marker lost';   s = '## Choosing a model';             t = 'Vyber modelu' }
    @{ n = 'heading marker kept';   s = '## Choosing a model';             t = '## Vyber modelu' }
    @{ n = 'bullet marker lost';    s = '- an item';                       t = 'polozka' }
    @{ n = 'table pipes lost';      s = '| a | b |';                       t = '| a b |' }
    @{ n = 'table pipes kept';      s = '| a | b |';                       t = '| x | y |' }
    @{ n = 'answer split lines';    s = 'One line only';                   t = ('Prvni' + $nl + 'druhy') }
    @{ n = 'collapsed long';        s = $long;                             t = 'Kratke' }
    @{ n = 'not collapsed long';    s = $long;                             t = $long }
    @{ n = 'echoed source';         s = 'This sentence is quite long enough to trip the echo gate.'; t = 'This sentence is quite long enough to trip the echo gate.' }
    @{ n = 'echoed short source';   s = 'Short one';                       t = 'Short one' }
    @{ n = 'dnt strict lost';       s = 'The JSON payload';                t = 'Datova zprava';        d = @('JSON') }
    @{ n = 'dnt strict kept';       s = 'The JSON payload';                t = 'JSON zprava';          d = @('JSON') }
    @{ n = 'dnt declinable ok';     s = 'a future llmster release';        t = 'budouci vydani LLMsteru'; d = @('llmster'); dec = @('llmster') }
    @{ n = 'dnt declinable strict'; s = 'a future llmster release';        t = 'budouci vydani LLMsteru'; d = @('llmster') }
)

$integrity = New-Object System.Collections.ArrayList
foreach ($c in $integrityCases) {
    $dnt = if ($c.ContainsKey('d')) { [string[]]$c.d } else { [string[]]@() }
    $dec = if ($c.ContainsKey('dec')) { [string[]]$c.dec } else { [string[]]@() }
    $r = Test-ChunkIntegrity -Source $c.s -Translated $c.t -DoNotTranslate $dnt -Declinable $dec
    [void]$integrity.Add([pscustomobject]@{
        name           = [string]$c.n
        source         = [string]$c.s
        translated     = [string]$c.t
        doNotTranslate = @($dnt)
        declinable     = @($dec)
        reason         = $(if ($null -eq $r) { $null } else { [string]$r })
    })
}

# --- translator note ----------------------------------------------------------

$noteCases = @(
    @{ s = 'The PATH guard'; t = 'Ochrana PATH (prelozeno z anglictiny)' }
    @{ s = 'The PATH guard'; t = 'Ochrana PATH (translated from English)' }
    @{ s = 'The PATH guard'; t = 'Ochrana PATH [preklad]' }
    @{ s = 'A note (prelozeno z anglictiny)'; t = 'Poznamka (prelozeno z anglictiny)' }
    @{ s = 'Nothing to strip'; t = 'Nic ke stripovani' }
    @{ s = 'Trailing dots'; t = 'Koncove tecky ...' }
    @{ s = 'Trailing dots ...'; t = 'Koncove tecky ...' }
    @{ s = 'Parenthetical kept'; t = 'Zavorka (jina poznamka)' }
)
$notes = New-Object System.Collections.ArrayList
foreach ($c in $noteCases) {
    [void]$notes.Add([pscustomobject]@{
        source     = [string]$c.s
        translated = [string]$c.t
        cleaned    = [string](Remove-TranslatorNote -Source $c.s -Translated $c.t)
    })
}

# --- listing lines --------------------------------------------------------------

$listingCases = @(
    'cmd/biotank/       host binary: serves the embedded client',
    'docs/demo/         design showcase - canonical visual reference',
    'go build ./...',
    'npm install',
    'src/main.go  x',
    'src/main.go        -race ./... -v',
    'plain prose with no path at all',
    'a/b/c   one two three',
    'a/b/c one two three',
    ''
)
$listing = New-Object System.Collections.ArrayList
foreach ($c in $listingCases) {
    [void]$listing.Add([pscustomobject]@{ line = $c; isListing = [bool](Test-ListingLine -Line $c) })
}

# --- source residue -------------------------------------------------------------

$residueCases = @(
    @{ n = 'clean';        s = 'On Windows you can also use the tool.'; t = 'Na Windows muzete take pouzit nastroj.' }
    @{ n = 'half done';    s = 'On Windows you can also use the tool to cross-build both binaries.'; t = 'Na Windows muzete pouzit to cross-build both binaries.' }
    @{ n = 'short term';   s = 'Uses Web Audio for sound.'; t = 'Pouziva Web Audio pro zvuk.' }
    @{ n = 'empty out';    s = 'Some source text here.'; t = '' }
    @{ n = 'protected';    s = 'Run `build.bat` to start.'; t = 'Spustte `build.bat` pro start.'; p = '`[^`]*`' }
)
$residue = New-Object System.Collections.ArrayList
foreach ($c in $residueCases) {
    $p = if ($c.ContainsKey('p')) { [string]$c.p } else { '' }
    $runs = @(Get-SourceResidue -Source $c.s -Translated $c.t -MinRun 3 -Protect $p)
    [void]$residue.Add([pscustomobject]@{
        name    = [string]$c.n
        source  = [string]$c.s
        output  = [string]$c.t
        protect = $p
        runs    = @($runs | ForEach-Object { [pscustomobject]@{ text = [string]$_.Text; words = [int]$_.Words } })
        gate    = [string](Test-SourceResidue -Source $c.s -Translated $c.t)
    })
}

# --- translation defects --------------------------------------------------------

$defectCases = @(
    @{ n = 'function word'; s = 'On Windows you can also use the tool.'; t = 'Na Windows you can also use nastroj.'; d = @('Windows') }
    @{ n = 'glue';          s = 'the presentation Bubble Surge is here'; t = 'prezentaciBubble Surge je zde'; d = @('Bubble Surge') }
    @{ n = 'camel in src';  s = 'call getUserName now'; t = 'zavolejte getUserName nyni'; d = @() }
    @{ n = 'clean';         s = 'A short sentence.'; t = 'Kratka veta.'; d = @() }
)
$defects = New-Object System.Collections.ArrayList
foreach ($c in $defectCases) {
    $found = @(Get-TranslationDefect -Source $c.s -Translated $c.t -DoNotTranslate ([string[]]$c.d))
    [void]$defects.Add([pscustomobject]@{
        name    = [string]$c.n
        source  = [string]$c.s
        output  = [string]$c.t
        dnt     = @([string[]]$c.d)
        items   = @($found | ForEach-Object {
            [pscustomobject]@{ kind = [string]$_.Kind; text = [string]$_.Text; words = [int]$_.Words; verdict = [string]$_.Verdict; reason = [string]$_.Reason }
        })
    })
}

# --- output leak ----------------------------------------------------------------

$longSrc = 'The configuration file controls how the engine reads documents from disk.'
$leakCases = @(
    @{ n = 'clean';            s = 'The build failed.'; t = 'Sestaveni selhalo.' }
    @{ n = 'empty';            s = 'The build failed.'; t = '' }
    @{ n = 'combining';        s = 'Direct from'; t = ("Primy z " + ([string][char]0x0336) * 60) }
    @{ n = 'repeated char';    s = 'Hello there'; t = 'Ahoooooooooooj' }
    @{ n = 'narration';        s = 'The build failed.'; t = 'To translate this text I will follow the rules.' }
    @{ n = 'meta words';       s = 'The build failed.'; t = 'Prelozte tento fragment do cestiny.' }
    @{ n = 'meta in source';   s = 'sending instructions to the console'; t = 'posilani instrukci do konzole' }
    @{ n = 'invented words';   s = 'One'; t = 'Choosing a domain name is one of the most important decisions you will make when starting a new website today.' }
    @{ n = 'short expansion';  s = 'FAQ'; t = 'Casto kladene otazky' }
    @{ n = 'long ok';          s = $longSrc; t = 'Konfiguracni soubor ridi, jak engine cte dokumenty z disku.' }
    @{ n = 'prompt echo';      s = 'The build failed.'; t = 'Reply with the translation only no commentary no quotes'; p = 'Reply with the translation only no commentary no quotes or explanations' }
)
$leaks = New-Object System.Collections.ArrayList
foreach ($c in $leakCases) {
    $p = if ($c.ContainsKey('p')) { [string]$c.p } else { '' }
    $r = Test-OutputLeak -Source $c.s -Translated $c.t -Prompt $p
    [void]$leaks.Add([pscustomobject]@{
        name   = [string]$c.n
        source = [string]$c.s
        output = [string]$c.t
        prompt = $p
        reason = $(if ($null -eq $r) { $null } else { [string]$r })
    })
}

# --- document structure ---------------------------------------------------------

$nl2 = "`n"
$structureCases = @(
    @{ n = 'identical';      s = ("# H" + $nl2 + '```' + $nl2 + 'code' + $nl2 + '```'); t = ("# H" + $nl2 + '```' + $nl2 + 'kod' + $nl2 + '```') }
    @{ n = 'lost heading';   s = ("# H" + $nl2 + 'text'); t = ('H' + $nl2 + 'text') }
    @{ n = 'odd fences';     s = ('```' + $nl2 + 'a' + $nl2 + '```'); t = ('```' + $nl2 + 'a') }
    @{ n = 'lost table row'; s = ('| a | b |' + $nl2 + '| c | d |'); t = ('| a | b |') }
)
$structure = New-Object System.Collections.ArrayList
foreach ($c in $structureCases) {
    [void]$structure.Add([pscustomobject]@{
        name   = [string]$c.n
        source = [string]$c.s
        output = [string]$c.t
        issues = @(Test-DocumentStructure -Source $c.s -Translated $c.t | ForEach-Object { [string]$_ })
    })
}

$doc = [pscustomobject]@{
    generatedBy  = 'tests\parity\markup\oracle.ps1'
    listing      = @($listing)
    residue      = @($residue)
    defects      = @($defects)
    leaks        = @($leaks)
    structure    = @($structure)
    reference    = 'ai-translation\scripts\markup-guard.ps1'
    protect      = @($protect)
    sentinels    = @($sentinels)
    placeholders = @($placeholders)
    integrity    = @($integrity)
    notes        = @($notes)
}

$json = $doc | ConvertTo-Json -Depth 10
[IO.File]::WriteAllText($Out, $json, (New-Object Text.UTF8Encoding($false)))

Write-Output ("wrote {0} ({1} protect, {2} sentinel, {3} placeholder, {4} integrity, {5} note cases)" -f `
    $Out, $protect.Count, $sentinels.Count, $placeholders.Count, $integrity.Count, $notes.Count)
