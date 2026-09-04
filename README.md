# BetterTranslator

A Windows desktop translation app for software teams. It translates words,
sentences and whole documents, and indexes the project you point it at so a
term already used in that project keeps its established wording. Models run
locally. WPF on .NET 10, shipping as a standalone executable with its own
visual identity rather than the stock WPF look.

```powershell
dotnet run --project src/BetterTranslator.App
```

A 1536 by 1016 window opens on a custom titlebar, with the chat sidebar, the
workspace mode slider and a working composer.

> [!NOTE]
> All nine build phases are complete apart from what needs a running model.
> The table below is the measured state. A section marked *(not yet built)*
> describes the intended shape rather than what ships.

## Status

| Area | State |
|---|---|
| Window shell, toolbar, mode slider, chat sidebar, persistence, composer, file preview | works |
| Translation, English to Czech, through the BetterRuntime CPU library | verified end to end in the running app |
| Engine port, all 49 reference scripts | decided: ported, recorded as a driver, or recorded as will-not-port with the reason |
| Whole-file translation from the command line | run with a real model over four Markdown documents, audited before and after |
| Whole-file translation from the window button | not yet run with a model behind it |
| Vulkan and CUDA offload | built, measured and wired. Never run on real hardware |
| Verification checks over every answer | works. The semantic checks need an optional model and stay skipped without it |
| Source-language picker | not built. Input is assumed to be English |

**The app translates.** Sending a message loads the chosen GGUF through the
BetterRuntime CPU library and writes the result back. **Fast and Thinking in the
composer choose the model**: Fast runs EuroLLM, Thinking runs TranslateGemma.
The choice is remembered.

**A chat's context is free by default.** Every send builds its whole prompt from
its own text, so a long chat costs no more than a new one. The plus menu's
**Memory** chip is the only thing that changes that. It adds this chat's earlier
translations plus passages retrieved from the indexed project or folder.

**Runtime backends.** The backend in force is picked under Settings > Runtime.
All three flavors (CPU, Vulkan and CUDA) have been built, measured and wired,
and every one can be downloaded from the installer or from its card in Settings.
Only the CPU path has been run. **Neither GPU offload path has been exercised on
real hardware.** The machine this was built on has an AMD Radeon, so CUDA has
never been loaded at all.

**The engine port is complete.** All 49 PowerShell scripts of the reference
engine are decided. Each one is ported, recorded as a driver over ported logic,
or recorded as will-not-port with the reason. The guard stack runs on every send
and can decline an answer. When it does, the entry says so, names the gate and
offers Retry. The result region shows the translation, a loading placeholder or
that failure. It never shows the source.

**Whole files can be translated**, keeping their line count. The preview's
Translate file button runs the document a paragraph at a time through that same
guarded translator. Every line and every unit now survives, and the figures are
in [docs/translation-audit/live-audit-report.md](docs/translation-audit/live-audit-report.md).

**An agent translates the way the window does.** The MCP server and the `bt`
command line build their jobs through the same factory the window uses, and cut
text up with the same router. A string sent by an agent takes the same route
through the same guards and comes back with the same notes. They read the same
formats, run the same verification, and store what they translate in the same
chat history.

Open questions and every conflict found against the design reference are
recorded in [DECISIONS.md](DECISIONS.md).

## Requirements

The .NET SDK 10.0 and Windows, because WPF is Windows-only. The app targets
`net10.0-windows10.0.19041.0`, so Windows 10 build 19041 or newer. The app also
carries the in-app agent server, so a framework-dependent run needs the ASP.NET
Core 10 runtime beside the Desktop runtime. The self-contained publish below
bundles both. Confirm the SDK with:

```powershell
dotnet --list-sdks
```

```text
10.0.302 [C:\Program Files\dotnet\sdk]
```

Any `10.x` entry works.

## Build, test and run

Run every step from the repository root.

1. Clone with the submodules. The repo carries two of them (`tools/ai.bat` and
   `tools/skills`). In an existing checkout, run
   `git submodule update --init --recursive` instead. Nothing in the build
   depends on either.

   ```powershell
   git clone --recurse-submodules <repository-url>
   ```

2. Build the solution.

   ```powershell
   dotnet build BetterTranslator.sln
   ```

3. Run the tests.

   ```powershell
   dotnet test tests/BetterTranslator.Tests
   ```

4. Start the app.

   ```powershell
   dotnet run --project src/BetterTranslator.App
   ```

```text
Build succeeded.
Passed!  - Failed: 0, Passed: 1020, Skipped: 13
```

The suite runs serially (`DisableTestParallelization`, see
`tests/BetterTranslator.Tests/Parallelism.cs`) and takes about fifteen seconds.
WPF's `Application` is process-wide, and the preview layout probe has to merge
the theme dictionaries into it. Run alongside other tests that read those
tokens, that turned about one run in three red. Ten seconds buys a
deterministic suite.

Thirteen tests are gated so the rest stays quick:

| Count | Gate |
|---|---|
| 8 | load a 2.3 GiB model from disk. See [Local inference](#local-inference) |
| 3 | launch PowerShell against the reference engine. See [Translation engine](#translation-engine) |
| 1 | reads whichever models happen to be installed on the machine |
| 1 | runs the corpus loop behind `BT_LOOP_CORPUS`. See [Translation engine](#translation-engine) |

The debug executable lands at
`src/BetterTranslator.App/bin/Debug/net10.0-windows10.0.19041.0/BetterTranslator.exe`.

## Local inference

Translation runs through **BetterRuntime**, a flat C ABI over llama.cpp built as
one self-contained DLL per backend. All three flavors exist and the loader
prefers them when present, in the order CUDA, Vulkan, CPU:

| Flavor | File | Install size | Needs beside it | Run on real hardware |
|---|---|---|---|---|
| CPU | `BetterRuntimeCPU.dll` | 3.4 MB | nothing | yes, end to end |
| Vulkan | `BetterRuntimeVulkan.dll` | 52 MB | `vulkan-1.dll`, from the GPU driver | no |
| CUDA | `BetterRuntimeCUDA.dll` | **631 MB** | `cublas64_13.dll` + `cublasLt64_13.dll` | no |

Sizes are measured byte lengths, not estimates. Each flavor is a separate
download rather than a setting, and every one is offered on every machine. The
ones the hardware cannot load are listed and locked, with the reason spelled
out, rather than hidden. See DECISIONS.md B46.

**Only CPU and Vulkan are self-contained.** The CUDA build imports
`cublas64_13.dll`, which imports `cublasLt64_13.dll`. That is 492 MB of CUDA
Toolkit redistributables that the NVIDIA *driver* does not install. Shipping the DLL
alone produced a runtime that could not load. The loader fell back quietly, so
the only symptom was that translation ran at processor speed. The two libraries
are declared as companions of the CUDA row and are fetched with it, so
installing CUDA now moves all three files or none. See DECISIONS.md B47.

**Every flavor needs the Microsoft Visual C++ runtime.** The library imports
`msvcp140.dll`, `vcruntime140.dll`, `vcruntime140_1.dll` and `vcomp140.dll`.
Windows does not supply these four files. Most machines have them, because many
applications install the redistributable. A machine without it loads no flavor
at all, and CPU is no exception.

The app looks for the four files in three places. It looks beside the runtime
library, in the `runtime-support` folder beside the executable, and on the
system search path. If all three fail, the app names the missing file and gives
the address of the Microsoft Visual C++ 2015-2022 Redistributable (x64). It also
says that a new install of the runtime does not add that file. That was the
wrong conclusion the earlier message invited.

`build.ps1` copies the four files out of the Visual Studio redistributable
folder into `artifacts/runtime-support`. The build then carries them into
`runtime-support` beside the executable. If the build machine has no
redistributable, the build says so and the executable uses the system copy.

The runtime panel names the flavor that **actually loaded**, not the one that
was chosen, and says so when they differ.

### The model runs in a child process

`src/BetterTranslator.Host/` is a small console executable that loads the GGUF
and does nothing else. The app starts it, talks to it over a named pipe, and
keeps every other part of translation in its own process: prompts, the guard
chain, terminology, segmentation.

It exists because the native runtime can call `abort()`. Measured, with a stack
from the dump: an assert inside `br_gen_start` raised a fast-fail, which no
managed handler can intercept. The window disappeared mid-translation with no
dialog and no log. In a child process that same abort ends a process that was
generating text. The send that hit it starts a fresh child and retries, so the
line still comes back. Verified by killing the child while a send was in
flight: that send returned a translation.

`BetterTranslator.Host.exe` is copied next to `BetterTranslator.exe` by the build.
If it is missing, the app falls back to running the model in-process, which is
what every version before this did. A packaging slip costs crash-resistance
rather than translation. See [DECISIONS.md](DECISIONS.md) D15.

`src/BetterTranslator.Runtime/Inference/BetterRuntime.cs` holds the P/Invoke
bindings and the managed surface (`BetterRuntimeModel`, with `Translate`,
`TranslateAsync` and a streaming `TranslateStream`). The native DLL is built out
of tree and is not checked in. The build copies it from `BetterRuntimeDir`,
which defaults to the local llama.cpp fork and can be overridden:

```powershell
dotnet build BetterTranslator.sln -p:BetterRuntimeDir=C:\path\to\betterruntime
```

The copy is guarded, so a machine without the DLL still builds. Only the gated
tests need it.

Those tests load a real GGUF and translate for real, so they are excluded from
the default run by an environment gate rather than a trait. An unfiltered
`dotnet test` would otherwise run them.

```powershell
# runs them (needs the model on disk)
$env:BETTERTRANSLATOR_MODEL_TESTS = "1"
dotnet test tests/BetterTranslator.Tests --filter "Category=RequiresModel"

# skips them, the default, no environment variable set
dotnet test tests/BetterTranslator.Tests
```

The model is `translategemma-4b-it.Q4_K_M.gguf` (2.3 GiB), found by globbing
`%USERPROFILE%\.lmstudio-shared\models`. Context length is pinned to 4096 rather
than the model's own 131072 maximum, which would allocate roughly 14.7 GB.

### Effort, context and Memory

The composer's effort control selects the model and its token budget, not just a
label:

| Effort | Model | Max tokens |
|---|---|---|
| Fast | EuroLLM | 512 |
| Thinking | TranslateGemma | 1536 |

The effort outranks the model picker in Settings, which is the fallback for when
the chosen effort's model is not installed. Both are found by file name across
the app's models folder and the shared LM Studio store. Neither composes a path
from a catalog id.

A send of more than one line goes through the document path, one sentence or
paragraph at a time. A gate that refuses one line costs that line rather than
the whole message. Choosing the source language as the target is refused before
the model loads, with the reason stated. When any gate declines an answer the
entry keeps its source **and says why** underneath. See
[DECISIONS.md](DECISIONS.md) B39 to B42.

**The Memory chip is the only switch.** Everything that brings project knowledge
into a send is behind it: this chat's earlier pairs, the passages retrieved from
the index, and the project's own glossary. With no chip the model sees the text and
nothing else. That holds line by line, so the hundredth sentence of a long
message is translated with exactly as clear a mind as the first. Verified
against the real model: the same sentence sent cold and sent again after eighty
unrelated sends comes back byte-identical.

When a gate refuses an answer the send is tried once more with a different
sampling seed. Only the seed moves, because this runtime seeds
deterministically and an identical request would return identical bytes. A
retry is a re-roll of the same request. See [DECISIONS.md](DECISIONS.md) B43.

**No glossary is in force until you write one.** A glossary is a claim about one
body of documents, not about a language. The shipped Czech file requires "store"
to become "úložiště", which is right for a model store and wrong for the shop Mr.
White went to last night. It ships as a worked example you can open, edit and
save from Settings > Config. Until you save a copy, nothing is applied.

Each send builds its whole prompt from its own text, so nothing accumulates
across a chat and no chat can leak into another. Attaching the plus menu's
**Memory** chip is the only thing that adds context. It adds this chat's earlier
source-and-result pairs, then passages retrieved from the indexed project or
folder. The block is capped at 3000 characters, so the material cannot push the
text out of its own context window.

The block is spliced ahead of the runtime's translator instruction rather than
beside the body. Placed after it, the model translates the block instead of
using it. See [DECISIONS.md](DECISIONS.md) B20-B23.

The backend in force comes from Settings > Runtime. See the status table at the
top for what is and is not built.

## Translation engine

`src/BetterTranslator.Engine/` is the translation engine, ported module by module
from the PowerShell scripts in the sibling `ai-translation` repository. It builds
to an ordinary `BetterTranslator.Engine.dll` that the app and the runtime
reference. There is no interop, no native toolchain and no extra build step.

All 49 files are decided. Each is either ported to a named module, recorded as a
driver whose logic lives in one, or recorded as will-not-port with the reason:

| Module | From | What it is |
|---|---|---|
| `Engine/Languages/` | `languages.ps1` | Registry, alias resolution, per-model filtering, script detection, the prompt shape each model was trained on |
| `Engine/Markup/` | `markup-guard.ps1`, `translate-final.ps1` | Protect and restore, sentinels, placeholders, chunk integrity, source residue, document structure, line parity, run boundaries, emphasis repair, unit fidelity, the document audit, prompt-leak detection, defect adjudication |
| `Engine/Slop/` | `rag.ps1`, `glossary-build.ps1`, `translate-sweep.ps1` | Symbol and phrase repair, do-not-translate lists, a document's own vocabulary, the glossary, whether a line is worth sending at all |
| `Engine/Memory/` | `rag-memory.ps1`, `rag-memory-verify.ps1` | Translation memory and the audit that re-tests it against gates fixed since it was written |
| `Engine/Documents/` | `translate-master.ps1`, `translate-words.ps1` | Paragraph segmentation, line restoration, word context. Driven by `Runtime/Inference/DocumentTranslator.cs` and `FinalRepairPass.cs` |
| `Engine/Models/` | `resolve-model.ps1`, `store-resolve.ps1` | GGUF metadata, chat templates, prompt building, model identity, where models already are |
| `Engine/Corpus/` | `rag-extract.ps1` | Which files of a folder become corpus, and whether an extract is text at all |
| `Engine/Terminology/` | none | The software-domain vocabulary: what a trade word means in the target language, and the corrector that fixes a named wrong rendering rather than refusing the line |
| `Engine/Chats/` | none | The instruction that asks a model to name a chat, and the test of whether what came back is a name |
| `Engine/Config/` | none | The shipped-default and user-override config store the above read through |

Every decision, including each will-not-port and its reason, is in
`docs/native-port/PORT-STATE.md`.

### Document fidelity options

`Engine/Config/PipelineOptions.cs` holds the document-fidelity switches. All are
on by default and each reverts with a single edit:

| Flag | What it does |
|---|---|
| `PreserveByteOrderMark` | A UTF-8 preamble found at read is re-applied at emit instead of being discarded by the reader |
| `TranslateFrontMatterProse` | Prose-valued YAML scalars become translation units. Keys, dates, identifiers and lists stay fixed |
| `RestoreAsciiPunctuation` | Typographic lookalikes in an answer are mapped back to ASCII for every class the source unit does not use |
| `TrimIntroducedTrailingBlanks` | Trailing blanks added during reassembly are removed, and only when the source declares none |
| `DropInventedMarkup` | A list marker or asterisk absent from the source unit is stripped from the answer rather than refused |
| `EscalateRetryDecoding` | The first attempt stays greedy. Later attempts lift temperature so a reseeded retry can return a different answer |
| `PreserveRunBoundaries` | A run is sent without its own leading and trailing whitespace, and the answer's edges are normalized back to the source span's |
| `RepairEmphasisRuns` | Whitespace the model inserted inside an emphasis run is removed, line by line against the source, skipping fenced and indented code |
| `FlagUnverifiedUnits` | A unit whose answer echoed its source, or kept a run of four or more source words, is counted on the result instead of passing unnoticed |
| `ProtectQuotedCitations` | A double-quoted span inside a prose run is lifted out with its quote characters, so a cited name or title is never sent |
| `RecoverEchoedUnits` | An echoed unit is retried run by run, but only when another unit in the same document translated, so an identity run stays a no-op |

### Fidelity checker and corpus harness

`tests/BetterTranslator.Tests/Loop/` scores a source and candidate pair against
six counted gates (protected tokens, completeness, residual source, structure
parity, byte hygiene, boundary spacing) and four 0 to 100 score metrics. Two
entry points, from the repository root:

```powershell
# scores one source and candidate pair, no model needed
dotnet test tests/BetterTranslator.Tests --filter FullyQualifiedName~TranslationLoopTests.ScorePair

# runs the pipeline over a corpus slice and scores it (loads a real model)
$env:BT_LOOP_CORPUS = "1"
dotnet test tests/BetterTranslator.Tests --filter FullyQualifiedName~TranslationLoopTests.RunCorpus
```

`BT_LOOP_SOURCE`, `BT_LOOP_CANDIDATE`, `BT_LOOP_MODEL`, `BT_LOOP_TUNING` and
`BT_LOOP_HELDOUT` override the paths and the slice sizes. Measured results are
in `docs/translation-engine-findings.md`, with the per-iteration record in
`docs/translation-loop-ledger.md`.

The PowerShell scripts stay the reference. Each module's oracle dot-sources the
script, records every answer it gives, and checks that output in:

```text
tests/parity/languages/oracle.ps1     regenerates the answers (read-only against the reference)
tests/parity/languages/oracle.json    those answers, checked in
```

The default suite compares the port against the fixture, which costs nothing. A
gated test re-runs the oracle and fails if the reference script has moved
underneath the fixture:

```powershell
$env:BETTERTRANSLATOR_PARITY_TESTS = "1"
dotnet test tests/BetterTranslator.Tests --filter "FullyQualifiedName~Parity"
```

```text
267 resolve cases, 0 mismatched      51 script patterns, 0 mismatched
11 protect cases, 0 mismatched       13 placeholder cases, 0 mismatched
33 integrity cases, 0 mismatched     216 recorded guard calls, 0 mismatched
```

The last line is a different kind of fixture and the most valuable one. The
reference's own test suite is a runner, not a library. There is no code in it
worth porting, but its INPUTS are drawn from damage that actually happened.

So they were recorded. Each reference guard is wrapped with a function that logs
its arguments and its answer, and the suite is run with those wrappers in place.
What comes out is a corpus nobody chose to suit the port. It immediately caught
one defect:
`[int]$x` in PowerShell **rounds** while `(int)x` in C# **truncates**, and every
threshold in the guards is written `[int](...)`. That moves the line between a
chunk that is accepted and one that is thrown away. Fixed in
`Engine/Text/PowerShellCast.cs`.

One deliberate divergence is recorded, in [DECISIONS.md](DECISIONS.md) B28. The
markup guard sorts ordinally rather than reproducing .NET Framework's NLS
collation. A structural gate whose verdict could shift with the machine's
culture would be a worse defect than a list in a different order. The verdicts
themselves match the reference word for word.

`pwsh` is not required. The oracle runs under Windows PowerShell 5.1. Nothing
under `ai-translation` is ever written to.

### The guards can be wrong, and when they are the reader pays

A guard that refuses a good answer costs the whole line: the entry keeps its
source text. Two such defects are recorded in [DECISIONS.md](DECISIONS.md) D12
and D13, both found by translating real pasted text rather than by reading the
code, and both fixed by correcting what the guard measures rather than by moving
a threshold.

A third defect of the same shape came from one word. Some words are the same in
English and in Czech, and `Test` is one of them. The guard compared the answer
with the source, found them equal, and refused a correct translation. The chat
then reported the whole message as untranslated. The rule now applies only to
text of two words or more. A single word that comes back unchanged is a
translation. A longer line that comes back unchanged is still a defect.

`tests/BetterTranslator.Tests/Fixtures/SpecCorpus.cs` is the corpus that found
them. It holds five families of the text people actually paste: a CLI
specification full of `--flags` and `<placeholders>`, policy blocks with
ALL-CAPS labels, mixed Markdown, an i18n JSON resource, and a paragraph naming
the projects. Its
assertions in `FidelityAsserts.cs` are properties rather than expected
translations, so they hold for every correct answer and pin no model's wording.
No clause of the source survives untranslated, every name and flag survives
byte-identical, and no sentinel reaches the reader.

Every unit a message is cut into now records what became of it. A line that keeps
its source carries the reason: the guard that refused it, or that the model
returned nothing, reflowed the answer, or echoed the source back. A partly
translated message can say which part and why instead of only how many.

Placeholders never reach the model. `<name>`, `{count}`, `%s` and HTML-ish tags
are lifted out of a chat line behind sentinels and put back afterwards, because a
model shown `<name>.<to>.<ext>` translates it to `<název>.<cíl>.<rozšíření>` and
no gate can see that anything is wrong. The protection is capped at eight per
line and the final retry always goes unprotected, so a model that will not
reproduce sentinels still gets to produce an ordinary translation. See
[DECISIONS.md](DECISIONS.md) D14.

## Publish

### One file, nothing beside it

The release artifact is a single `BetterTranslator.exe` and nothing else. A
native C++ bootstrap carries the whole published application inside itself in a
PE section named `.btpay`. It extracts that payload to
`%LOCALAPPDATA%\SZEINER\BetterTranslator\runtime\` on first run, then starts the
.NET runtime in its own process through `hostfxr`.
One process, one taskbar entry, no .NET runtime and no Visual C++ redistributable
needed on the target machine.

```powershell
pwsh -File build-singlefile.ps1
```

```text
BetterTranslator -> bin\Release\BetterTranslator.exe
```

In Visual Studio, pick **SingleFile** in the configuration dropdown and press
Build for the same artifact. Set `BetterTranslator.SingleFile` as the startup
project once. F5 then builds it and starts the packed exe itself, with no
console window and nothing wrapping it. Debug and Release are untouched. The
chain is carried by `build\BetterTranslator.SingleFile.csproj`, which is the only
project that configuration builds.

The exe is self-contained but not self-sufficient on first run. The inference
runtime and the translation models are **not** inside it and are downloaded on
first use, so a fresh machine needs internet once. It is also unsigned unless you
build with a certificate. SmartScreen will warn the first person who runs an
unsigned copy downloaded from the internet.

That build needs Visual Studio with the **Desktop development with C++** workload
in addition to the .NET 10 SDK, because the bootstrap is C++. It is the only part
of this repository that needs a native toolchain. It sits deliberately outside
`BetterTranslator.sln`, so `dotnet build BetterTranslator.sln` keeps working
without one (`DECISIONS.md` B24).

Measured on this machine: 333 MB of payload compresses to a 106 MB executable,
cold start extracts it in about 1.5 s, and warm start adds a median of 11 ms
before the runtime loads. The flags (`--bt-verify`, `--bt-selftest`,
`--bt-clear-cache`, `--bt-cache-path`), the container format, the exit code table
and the full measurements are in
[docs/build/single-exe.md](docs/build/single-exe.md).

Pass `-CertificateThumbprint <sha1>` or `-CertificatePath <pfx>` to sign the
result. Signing runs after the payload section is appended, never before, and is
skipped cleanly when no certificate is configured.

### The .NET single file publish

Still produced, unchanged, and still the input the bootstrap is built from. For a
single self-contained file that needs no .NET runtime on the target machine, but
does drop `cli\` and the inference host beside itself:

```powershell
dotnet publish src/BetterTranslator.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

```text
BetterTranslator -> src\BetterTranslator.App\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\
                    BetterTranslator.exe
                    cli\bt.exe
```

`cli\bt.exe` is the command line, carried along so an agent can be pointed at a
file that ships with the application. It is the published self-contained `bt`
when one has been built. Otherwise it is the framework-dependent build output.

Drop `--self-contained true` for a much smaller build that requires the .NET 10
Desktop and ASP.NET Core runtimes on the target machine.

The command line publishes the same way:

```powershell
dotnet publish src/BetterTranslator.Cli -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishDir=bin/Release/net10.0/win-x64/publish/
```

```text
bt -> src\BetterTranslator.Cli\bin\Release\net10.0\win-x64\publish\bt.exe
```

The runtime identifier belongs on the command line, not in a publish profile. A
profile sets it as a project property, which never reaches the referenced
inference host, and a self-contained executable may not reference one that is
not (NETSDK1150).

`pwsh -File build.ps1 -Publish` produces both, command line first, so the app
carries it into `cli\` beside the published `BetterTranslator.exe`.

## Agents and scripts

Two surfaces sit beside the window, and both call the same services it does.

**An AI agent** connects to the MCP server hosted inside `BetterTranslator.exe`.
Settings > Agent switches the listener on and off without a restart, and shows
the one command to paste where the agent runs:

```powershell
claude mcp add --transport http bettertranslator http://127.0.0.1:8765/mcp
```

It binds `127.0.0.1` by default, so no domain, no certificate and nothing
published is needed. The agent runs on the same machine and reaches loopback
directly. Any other host reaches the machine from the network, so a bearer token
becomes required and the server refuses to start without one.

An agent that speaks MCP over a pipe rather than over HTTP needs no address at
all, and does not need the window open. It starts `bt` itself.

```powershell
claude mcp add -s user bettertranslator -- "C:\path\to\bt.exe" mcp
```

Give the full path. A bare `bt` resolves only where it is on `PATH`, and an
agent that cannot find it reports `connection closed` with nothing else to go
on. A build puts a copy of the command line beside the application, in
`cli\bt.exe` next to `BetterTranslator.exe`. Settings > Agent can then render
that command with a path that is really there. Drop `-s user` to register it for
the current project only.

When a client reports `connection closed`, read the first line `bt mcp` writes
to stderr: it names the version, the build time and the path of the executable
that answered. A build time older than the last change to this repository means
the agent is running a stale copy.

`bt mcp` serves the same ten tools on stdin and stdout. `show_in_gui` is the one
that differs. With no window running it reports that it is unavailable rather
than pretending. Settings > Agent shows both registration commands with a copy
button.

Ten tools are exposed and no others: `translate_text`, `translate_file`,
`translate_batch`, `job_status`, `job_cancel`, `list_languages`, `list_models`,
`select_model`, `show_in_gui`, `get_entry`. Each carries a description, an input
schema with per-parameter descriptions, an output schema and its annotations. An
agent needs no documentation beyond `tools/list`.

**An agent translation is the same translation the window makes.** Both surfaces
build the job through one factory and cut the text up with one router, so the
same string produces the same prompts either way. Routing is by what is in the
text: JSON, then Markdown, then prose. Text is sent a unit at a time, a line and
then a sentence, rather than as one all-or-nothing generation. The stored
temperature and the standing instruction from Advanced travel with it, and the
same post-generation verification runs over what comes back. What the reassembly
did to the text arrives as a `note`: lines that kept their source, blocks
retranslated phrase by phrase, spans the verifier flagged.

Two things an agent asks for rather than gets by default. `use_memory` on the
translate tools (`--memory` on `bt`) is the composer's Memory chip for a caller
with no composer. It brings this project's indexed passages and its glossary
into the send, and like the chip it is off unless asked for. A glossary is a
claim about one body of documents, and applied to general prose it refuses
correct translations. `.pdf` and `.docx` are read with the readers the window
uses and written back as text, so `terms.pdf` becomes `terms.pdf.cs.txt`.
Neither format can be written back.

Every agent translation is stored the way the window stores one: a chat per
agent session, named after the first thing in it and then renamed by the model
that just translated, and an entry per translation.
`translate_text` hands back an `entry_id`. `get_entry` and `show_in_gui` then
work on an agent's own output, not only on rows the window wrote. A window that
is open picks the row up without a restart.

**A script** uses `bt`, which is headless and needs neither the window nor the
MCP endpoint:

```powershell
bt translate "The build is green." --from en --to cs
echo Hello | bt translate --from en --to cs
bt translate --file README.md --to cs
bt translate --batch .\docs --to cs --out .\docs-cs
bt translate --file terms.pdf --to cs        # writes terms.pdf.cs.txt
bt translate "The build is green." --to cs --memory
bt languages --json
bt mcp
bt models --select EuroLLM
```

stdout carries the translation or the JSON envelope and nothing else. Progress
goes to stderr. Exit codes are 0 success, 2 usage, 3 input missing, 4 runtime
unreachable, 5 translation failed. `bt --help` documents all of it.

## Repository layout

| Path | Contents |
| --- | --- |
| `BetterTranslator.sln` | Solution, at the repo root |
| `src/BetterTranslator.App/` | The WPF app: views, view models, controls, themes |
| `src/BetterTranslator.Core/` | Models, SQLite store, shared services. References no WPF assembly |
| `src/BetterTranslator.Engine/` | The translation engine ported from the `ai-translation` PowerShell scripts, plus the structure-preserving paths in `Markdown/` and `Json/` (detection, segmentation, sentinel guarding, reassembly). Builds to `BetterTranslator.Engine.dll`. References Core, and Markdig for real source spans |
| `src/BetterTranslator.Indexing/` | Readers, Markdown block model, chunker, index store and the indexing pipeline. References Engine for corpus selection and the extraction quality gate |
| `src/BetterTranslator.Runtime/` | Download manager, install paths, model catalog, the guarded translator, BetterRuntime P/Invoke bindings, the client half of the inference host, and the agent gateway both agent surfaces call |
| `src/BetterTranslator.Host/` | `BetterTranslator.Host.exe`: the child process the model is loaded into, so a native abort cannot take the window with it. Copied beside the app by the build |
| `src/BetterTranslator.Cli/` | `bt.exe`: the headless command line for scripts. References Core, Engine and Runtime, never the app |
| `src/BetterTranslator.Map/` | Retrieval map world model, camera and Skia paint pass |
| `native/BetterTranslator.Bootstrap/` | The C++20 bootstrap that becomes the shipped `BetterTranslator.exe`: PE payload lookup, container reader, per-user cache, `hostfxr` start. Built by `build\SingleFile.targets` through MSBuild.exe, not by `dotnet build` |
| `tools/BetterTranslator.Packer/` | The packer: reads a publish tree, writes the container, appends the `.btpay` section, patches the PE headers. Also the only managed reader of the container, which is what the round-trip tests use |
| `build/` | `SingleFile.targets` (publish, native build, pack, sign), `FormatConstants.targets` (generates the C# view of `btpay_format.h`), `run-singlefile-checks.ps1` (the evidence run, on a desktop of its own), and `BetterTranslator.SingleFile.csproj`, the only project the **SingleFile** solution configuration builds: it runs the chain, and its launch profile starts the packed exe, so Build and F5 in Visual Studio give you the artifact and then run it |
| `docs/build/single-exe.md` | Container format, cache layout, bootstrap flags, exit codes and the measured startup numbers |
| `tests/BetterTranslator.Tests/` | 1,746 tests across 164 files: tokens, time, readers, sentence splitting, casing restoration, translation verification, the verification gate and its semantic stage, the component installer, Markdown and JSON parsing, round-trip and fallback, preview, sizes, URLs, transfers, inference, runtime flavors and what may be offered, chunker, indexing, translation context, translation state and its loading placeholder, document translation, line and emphasis parity, quoted citations, the document audit, the app icon and its wiring, the ported engine's guards and stores, the fidelity checker and corpus harness under `Loop/`, plus 41 skipped gated tests |
| `DECISIONS.md` | Open decisions, design-reference conflicts, build decisions |
| `tools/icons/` | SVG to XAML icon converter, the app icon generator and their notes. Neither is in the solution |
| `docs/app-icon.md` | Which surface draws the app icon, at which frame size, and how it is generated |
| `tools/ai.bat/` | Submodule: [Deerpfy/ai.bat](https://github.com/Deerpfy/ai.bat), a CLI launcher for local AI models. Not part of the build |
| `tools/skills/` | Submodule: [Deerpfy/skills](https://github.com/Deerpfy/skills), 70 Claude Code skills. Surfaced to the session as directory junctions under `.claude/skills/`. Not part of the build |
| `docs/research/` | Sourced digests that bind UI work |
| `docs/eu-acts/` | Raw EU act and ISO standard texts, cited as source of truth by the compliance skills in `tools/skills/` |
| `tests/parity/` | Parity oracles and fixtures: what the PowerShell reference actually answers |
| `tests/singlefile-probe/` | A stand-in managed entry point used only by the single file evidence run, to check argument forwarding and exit code propagation without opening a window |
| `docs/native-port/` | State file for the in-progress PowerShell-to-C# engine port |
| `CLAUDE.md` | Repository-wide agent rules and rule routing |
| `src/CLAUDE.md` | Constraints for the application tree |

Inside `src/BetterTranslator.App/`: `Themes/` holds the token and
control-template dictionaries, `Views/` the windows and user controls,
`ViewModels/`, `Controls/`, `Converters/` and `Services/`. `Assets/` holds the
app icon master and the `.ico` generated from it.

## Not in version control

`bin/`, `obj/`, `.vs/` and `*.user` files. Model artifacts (`*.gguf`, `*.safetensors`, `*.part`) are ignored as a guard. They normally land in the app data folder, outside the repo entirely. `prompts/`, `.claude/` and a root-level `ai.bat/`
checkout are local authoring aids, ignored by git and absent from a fresh clone.
The app builds without them. The submodule at `tools/ai.bat` is exempt from that
ignore rule and is tracked.

Runtime data lives outside the repo in `%LOCALAPPDATA%\BetterTranslator`:
`betterTranslator.db` for chats and entries, plus `models/` and `cache/`. The
database runs in write-ahead logging mode, because the window and an agent can
both be writing to it, so `betterTranslator.db-wal` and `-shm` sit beside it.
They are derived state. Moving the data folder settles them into the database
first and leaves them behind rather than copying them.

## What works today

- **Window shell.** Custom 32 pixel titlebar over `WindowChrome`, so eight-handle
  resize and Aero Snap still come from Windows. Maximize bounds do not: a
  maximized window hangs a resize border off every edge of the screen, and the
  content is inset by the measured overhang so none of it lands outside. Clamps
  at 880 by 620. A size pill shows during a resize drag and clears after.
  Settings sits with minimize, maximize and close, in the same caption geometry.
- **Toolbar.** Outlined sidebar and right-panel buttons, each taking the accent
  while what it toggles is showing.
- **Scope** *(built, Project and Folder currently locked)*. The project selector
  opens a Project, Folder or None menu, with the setting in force naming itself
  and the chevron turning while it is up. It closes on a second click, on a
  click anywhere else, on Escape and when the window loses focus. Project and
  Folder each carry a padlock and the reason, and choosing one does nothing. The
  gate is on the command as well as the row, so the first run's two scope tiles
  are locked too. None stays open because it is the setting in force. Behind the
  lock: Project and Folder open the real Windows folder dialog, cancelling leaves
  the scope alone, and a chosen scope replaces the selector with an accent chip
  carrying the scope kind, the path and a clear button. Scope defaults to None
  and never gates translation.
- **Memory workspace** *(built, currently locked)*. Its own sub-toolbar: a way
  back to the chat, Overview and Retrieval map, Add sources, and the indexed
  status read off the rail's own rows rather than counted twice. A rail of
  project sources and learned terms, a centered empty state until something is
  indexed, and an add-sources dialog whose Start indexing stays disabled until a
  source is picked. The mode segment is locked, so none of this is reachable in
  a build as it ships. The composer's Memory chip is a separate thing and is not
  affected.
- **Workspace mode slider.** Memory, Simple and Advanced, with a thumb that
  animates both position and width between segments of different sizes. Memory
  carries a padlock and the reason it is locked, rather than being hidden.
- **Chat sidebar.** Collapsible sections with counts, a search field that wipes
  across the New chat button, and relative timestamps driven by one shared
  five-second clock rather than a timer per row. Each row carries a menu button
  that appears on hover, offering Pin to top, Rename and Delete. Right-clicking
  the row opens the same menu. The open row is gray rather than accented.
  Opens and closes on a width animation that clips the panel rather than
  reflowing it, and stays resizable by its separator afterwards. A divider marks
  its edge against the chat, strengthening while the separator is grabbed. A
  chat's name lives here and here only. The chat itself is headed by its stamp.
- **Chats name themselves.** Sending the first message into a new chat raises a
  loading placeholder where the name goes, and once that message has been
  translated the same resident model fills it in. The name is a summary of at
  most five words and 29 characters, written in the language the message was
  translated *into*: Czech when translating to Czech, German when translating to
  German. It asks the model for a title. Where a translation fine-tune ignores
  that instruction it translates the truncated first line instead, which still
  leaves the sidebar reading in the chosen language. The placeholder is why the row never shows the
  truncated English on its way there, and 29 is what the row fits before it
  ellipsizes. A name typed by hand is never overwritten, the attempt never delays
  the composer, and a chat that could not be named simply keeps its truncation
  with nothing said about it. Recorded as [DECISIONS.md](DECISIONS.md) D6.
- **Chat entries.** Every entry sets its source against its result: the source is
  smaller and gray, the result larger and full strength. A sentence puts a rule
  between the two. A word keeps an arrow. Both are headed by the language they
  were translated into, recorded on the entry when it was sent rather than read
  from the composer afterwards. Hovering a column reveals that column's own copy
  button, which confirms with a green tick. Entries written before the language
  was recorded head neutrally.
- **Ordinary messages.** Translated a line at a time, and a line that comes back
  unusable is retried sentence by sentence. Line first, because a line is usually
  a whole thought and context makes a better translation. Sentences only on
  failure, because less context per call is worth far less than a paragraph left
  untranslated. A one-line message is still one call with the whole text. Two
  gates calibrated over document lines are relaxed for a unit somebody typed.
  The word-count ceiling turns proportional at twenty source words rather than
  eight, and a balanced pair of brackets may be added where the source had none.
  Both gates refused correct translations of short sentences, and neither can
  corrupt anything when there is no document structure around them. Both stay
  strict for the file path. Casing the source carried is restored afterwards
  rather than asked for, so a shouted line stays shouted and a `FEATURE:` label
  comes back `FUNKCE:`. The model keeps that shape only sometimes, and a source
  that was not shouting can never make an answer shout.
- **Markdown messages.** A message written as Markdown is translated as the same
  document: it is parsed, only its prose is sent to the model, and each answer is
  spliced back over the text it replaced. Nothing is re-rendered from a syntax
  tree, so headings, lists, tables, quotes, fences, link and image targets, raw
  HTML and front matter come back byte-identical. Link text and image alt text
  are prose and are translated. A sentence running through bold and a link is one
  call, not four: the markup is lifted out behind numbered sentinels the model
  carries through, and a block whose answer drops or reorders one is retranslated
  phrase by phrase instead. Such a message renders formatted by default and
  carries a View/Source switch that moves both columns together. Copy has always
  put the raw text on the clipboard, which for these is the raw Markdown. An
  ordinary message gets neither and takes the path it always took. So does a
  message that renders as Markdown but holds no prose to send: a pasted fenced
  block, or a brief wrapped in angle-bracket tags that CommonMark reads as raw
  HTML.
- **JSON messages.** A resource file, pasted into the composer or attached as a
  `.json` file, is translated value by value, never as a blob. Keys are not
  protected from the model, they are not sent to it. Only string values travel,
  and each answer is spliced back over the bytes its value occupied. Key order,
  nesting, indentation, minification, numbers, booleans and nulls are identical
  by construction. Interpolation slots
  (`{count}`, `{{name}}`, `%s`, `%1$s`), HTML-ish tags, URLs, hex colors and
  embedded line breaks are lifted out behind numbered sentinels and put back
  afterwards, so a translated string still formats against the arguments the
  code passes it. Accented characters are written the way the file writes them:
  literally, unless the file itself uses `\u` escapes. Values are batched for
  throughput and mapped back by span, so two keys holding the same word get
  their own answers. A batch that comes back wrong is retried one value at a
  time, and a value nothing usable came back for keeps its source and is named
  under the entry. The message shows as keys against values, with the same
  View/Source switch, and Copy gives raw JSON that reparses. Text that merely
  begins with a brace is not a document and takes the path it always took.
- **Waiting for a translation.** The result side shows a placeholder while the
  model works: bars for the lines the answer will occupy, swept by one moving
  mask. It never shows the source text. It is held back 150 ms so an answer faster
  than that never flashes one, and held on 350 ms once shown so a slower one
  cannot flicker it away. With Windows animation effects off the bars are static
  instead. A send that comes back with nothing replaces the region with what went
  wrong and a Retry, which supersedes anything still in flight for that entry.
- **Confirmations.** A layer inside the window rather than a second one, naming
  the specific action. Escape cancels, Enter takes the action, and both buttons
  carry a glyph from the icon set.
- **Persistence.** Chats and entries are stored in SQLite and reload on restart.
- **Composer.** Rests two lines tall and grows from there, stopping at a maximum
  where it scrolls instead. A toolbar that never wraps: attach menu, language
  pill with composed flag artwork, and send. Enter translates, Shift+Enter adds
  a line. One line of chrome underneath carries both the shortcuts, as drawn
  keycaps rather than a sentence, and the AI notice. The notice drops to a
  second line rather than being clipped when the column gets narrow enough.
- **Advanced panel.** Opens and closes on the same width animation the sidebar
  uses and is resizable by its own separator. Carries its own header and close,
  the model choices with their notes, temperature with a monospaced readout, the
  user prompt, and a reset ruled off from the settings it undoes.
- **Layout.** The chat column holds its reading measure when there is room and
  gives it up when a panel opens or the window narrows, down to the 880 by 620
  clamp, without clipping or a horizontal scrollbar.
- **Attachments.** Project memory always leads. The same file attached twice
  produces two chips, and each removes only itself.
- **File preview.** Reads real PDF, DOCX, MD, JSON and TXT with the same readers
  the agent surfaces use, so a file translated by an agent and one dropped into
  the window start from the same text. Markdown offers Formatted and Source. The
  others show one view and no switch, JSON monospaced so its indentation stays
  legible. Choosing the Translated version selects what to display and starts
  nothing. Running the file is its own button, below.

- **Translating a whole file.** Translate file runs the previewed document and
  gives back its exact line count. Consecutive wrapped lines are joined into a
  paragraph so the model has a sentence to work with, and the answer is wrapped
  back onto the lines it came from. A paragraph that cannot be is refused rather
  than written short. Code fences, tables and markup are never sent. Every unit
  goes through the same guarded translator a chat message uses, so a document
  gets exactly the same gates. A finishing pass then repairs what no gate could
  see: a clause left in English, a word welded to a protected term. It asks the
  model to judge only the cases no rule can settle. It reports what it did: how
  many lines were translated, how many kept their source, how many paragraphs
  were joined. The result is shown, not written over the file.

- **First run.** On a clean profile a modal opens over the chat and does not
  close until a runtime and a model are present. The install location can be
  redirected, and cancelling that dialog changes nothing. The action button prices
  the real selection, and a pasted link is refused by naming what was wrong
  with it. Its closing "where to start" step offers Just chat, Repository and
  Folder. The last two are locked with the project scope they lead to, so all
  three land on an empty chat.
- **Downloads.** Real streaming to a real path, with a live rate and a
  remaining time computed from what is actually left. A cancelled or failed
  transfer leaves nothing behind that looks installed, and the runtime is never
  removable.
- **Inference, out of process.** A local runtime host and an endpoint for one
  completion and one embedding. Unreferenced by the send path since translation
  moved in process. See B18 in [DECISIONS.md](DECISIONS.md).

- **Model downloads.** The catalog carries real download links, so D7 is
  resolved. Before any socket opens the resolver asks whether the model is
  already on the machine, including in the shared LM Studio store, and says so
  rather than fetching it again. A transfer resumes from its `.part` over a
  Range request, is hashed as the bytes stream past, and is promoted into place
  only after its length, GGUF header and checksum check out. Until then it stays
  a `.part` that cannot be mistaken for an installed model. An HTML body on a
  200 is treated as Drive's quota page, not as content. Downloads of one
  artifact are serialized across processes by a lock file. One connection is
  used deliberately. The link measured ~14 MiB/s on a single connection, and
  two, four and eight gave the same total, so no connection pool ships. See B19.

- **Inference, in process.** BetterRuntime loads a GGUF model through a flat C
  ABI and returns a translation, streamed or one-shot, with UTF-8 codepoints
  reassembled across token boundaries so diacritics survive. Sending a message
  starts it, because loading a model costs seconds and gigabytes and most
  sessions open the window before translating anything.

- **Runtime and model choice.** Settings > Runtime reports what the machine can
  run and lists all three flavors whatever it finds. It probes by asking whether
  the CUDA and Vulkan loaders resolve, not by reading a device name. A flavor the
  hardware cannot run says so and cannot be selected. One that is runnable but
  absent carries a **Get** button that hands the download to the installer.
  Those are different sentences and read differently. The choice is remembered.
  Changing it asks for a restart, because Windows will not swap a native library
  that is already loaded. The model list is every GGUF found in
  the models folder and the LM Studio shared folder.

- **Effort, and what the model is allowed to see.** Fast and Thinking select the
  model and its token budget, and the choice survives a restart. Every send
  builds its whole prompt from its own text, so a chat's context stays free
  however long it runs and one chat cannot leak into another. Checked against
  the real model, where the same sentence sent three times comes back identical.
  Attaching the plus menu's Memory chip is the only way to add anything: this
  chat's earlier translations first, then passages retrieved from the indexed
  project or folder, capped so they cannot crowd out the text. Removing the chip
  frees the context again on the next send.

- **Indexing.** Points at folders, repositories or individual files, reads
  their real content, chunks on the most natural boundary available and writes
  to the index. Counts start at zero and climb to the true total. Every figure
  on screen is read back from the index. The work runs off the UI thread and a
  cancel stops within one chunk.
- **Add to this project.** The action button names the real selection, for
  example "Index folder and 3 files". Both chat-derived rows state why they are
  unavailable when no chat exists.

- **A menu on the message.** Each row carries its own overflow menu, also
  reachable by right-clicking the row's footer: copy the source, copy the
  translation, or delete the message. Copy grays itself out when there is
  nothing to copy, rather than accepting the click and doing nothing. Delete
  confirms first, then takes that one row out of the conversation and out of
  storage, leaving the chat and its other messages alone. It is the only way to
  remove a single message. Before it, the smallest thing that could be deleted
  was a whole chat.

- **What a message cost.** In Advanced, each result carries the tokens it
  generated and how long it took, and the chat carries the total. Only measured
  figures are shown: generated tokens are a real count of decode steps, and the
  prompt side is absent because the runtime exports no tokenizer. A number
  estimated from character length sitting beside a measured one would be
  indistinguishable from it.

- **How much of it came back translated.** Under the cost, in smaller and
  lighter type, Advanced states the same pair measured: coverage as a
  percentage, then what the audit found. Line and unit counts appear only when
  the two sides disagree. An equal pair is the expected outcome, and it is the
  widest thing the row can hold for the least it can say. A kept source span, a
  lost citation or a markup defect is named, and it is the only thing here that
  leaves the gray. A clean entry says "no defects" rather than leaving a reader
  to infer it. `Engine/Markup/TranslationAudit` computes it from the source and
  result alone, with no word list, and nothing is computed while Advanced is
  off.

- **Checks on every answer.** A gate of checks reads each translation before the
  window shows it. The checks look at structure, coverage, length ratios,
  terminology and how natural the target language reads. A check that cannot run
  gives its reason. It never reports a pass. The window draws the defects under
  the words. An agent gets the same result as data.

- **The semantic checks, and the model they need.** Three checks compare
  meaning. The first measures how similar the source and the translation are.
  The second translates the answer back into the source language. The third
  compares that reverse translation with the source. Each call on a model costs
  time, so these checks read only the spans that an earlier check marked. They
  read at most 24 spans in one answer.

  The checks need a sentence embedding model. It is
  `intfloat/multilingual-e5-small`, published under the MIT license, and 465 MB
  with its tokenizer. The model is optional. It is one row in the download list,
  and no install selects it for you. Before the app puts the two files in place,
  it compares their length and their SHA-256 with the catalog. A download that
  fails that comparison leaves nothing behind. Until you install the model, the
  three checks report `skipped` and give the reason.

  Each reverse translation is one call on the model that is already loaded.
  **Reverse translations per answer**, in Settings > Downloads, sets the limit.
  The default is 24. Set it to 0 to stop the reverse translations. The
  similarity check still runs, because it reads the embedding model and writes
  no text. The tokens and the time of each reverse translation go into the
  figures the entry already shows. There is no second number to add up. The
  result carries a line such as `3 of 7 flagged spans re-checked, cap 24`.

- **Runtime controls in the caption.** A status panel beside the download manager
  reports what the runtime is doing, with pause available while a generation is
  running and eject available once a model is loaded. Pausing stops the
  generation and keeps the model, so the next send starts at once. Ejecting is
  the one that frees the gigabytes a loaded model holds, without closing the app.

- **Editable configuration.** Settings > Config edits the engine's configuration
  in place: languages, model prompts, prompt fragments, terminology and the
  Czech glossary. It opens any of them full-size in its own editor, and resets
  one back to the shipped copy. An edit is validated before it is saved, so a
  file that would not parse is refused rather than written and discovered at the
  next translation. The shipped copies stay in the assembly. An override is a
  file beside the database, and resetting deletes it. The glossary is the one whose
  shipped copy is an **example rather than a default**: nothing is applied until
  you save one of your own.

- **Language picker.** A searchable list of every language the registry carries,
  all 51 with composed flag artwork, drawn as vector geometry rather than
  imported as images or borrowed from an emoji font, which ships no country flags
  on Windows. A canonical code badge in the same tile is the fallback for a code
  the flag table does not carry, so no row can render empty. Search matches the
  English name, the endonym or the code. A language the selected model does not
  support is not hidden. It stays on the list, cannot be chosen, and says why. EuroLLM publishes its 35 trained
  languages inside the model file, and 15 of the 51 rows fall outside them.
  TranslateGemma publishes no list at all, so its rows read as unverified rather
  than supported. Offering a language a model never saw produces something
  structurally perfect that no gate can fault, which is why this is the one
  place it can be prevented. Sources per language: `docs/language-coverage.md`.

The retrieval map is *(not yet built)*.

## Icons

`src/BetterTranslator.App/Themes/Icons.xaml` is generated from the shipped icon
set. Do not hand-edit it. See [tools/icons](tools/icons/README.md) for the
converter and for the problems found in the source set. An icon the set does not
carry, or one of its files that needs correcting, goes in `tools/icons/overrides/`
as an SVG. Anything added to the generated XAML directly lasts until the next run
and then disappears without a build error.

The application icon is one master, `src/BetterTranslator.App/Assets/app-icon.png`,
and one generated `app-icon.ico` beside it carrying nine frames from 16 to 256
pixels. The generator measures the master's bars and redraws each frame at its
own pixel size, so no frame is a resampled bitmap. It is wired to the executable
through `ApplicationIcon` and to the running window through `MainWindow.Icon`, so
the taskbar, Alt and Tab, Explorer and the title bar mark all draw the same
artwork, on the same `#F3F3F3` ground. Regenerate it with the command below.
The full record is in [docs/app-icon.md](docs/app-icon.md).

```powershell
dotnet run --project tools/icons/AppIcon -c Release
```

## Constraints

Change any of these only through an explicit decision:

- `OutputType` is `WinExe`. `Library` produces no executable, and plain `Exe`
  attaches a console window to a GUI app.
- The target is `net10.0-windows10.0.19041.0` with `UseWPF`, `Nullable` and
  `ImplicitUsings` enabled. The platform version is pinned so SkiaSharp.Views.WPF
  resolves its .NET 10 assets instead of falling back to .NET Framework.
- The stack is WPF on .NET 10. No WinForms, WinUI 3, MAUI or Avalonia.
- The solution holds exactly one `WinExe` startup app. Other projects are
  libraries.
- `BetterTranslator.Core` references no WPF assembly.
- Code-behind carries wiring and view-only concerns. Logic belongs to view
  models and services.

## Design system

BetterTranslator ships its own visual identity. Untouched system control
templates, `SystemColors` chrome and a template-fresh `MainWindow` count as a
failure state rather than a starting point. Every color, size, radius, duration
and easing value in a view resolves to a token defined under `Themes/`, and no
view markup carries a free-hand literal. A test asserts that every documented
token key resolves to its documented value, so a drifted token fails the build
rather than falling back silently.

Read the digests in [docs/research](docs/research) before UI work. The routing
table in [CLAUDE.md](CLAUDE.md) maps each task type to its binding rule
sections, and [src/CLAUDE.md](src/CLAUDE.md) carries the constraints for the
application tree.
