# Managed to native boundary: CPU-only osx-arm64 verdicts

Scope: the wrapper under `mac/`. Windows behaviour is unchanged. Verdicts are NO CHANGE, CHANGE REQUIRED or NEW. Every row was read in the file cited.

## 1. Library naming and resolution

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| Logical DllImport name `"BetterRuntime"` (`BetterRuntime.cs:78`) | one logical name, resolver maps to a flavour file | same logical name; only the physical file differs | NO CHANGE | none |
| `lib` prefix and suffix | candidate built as `flavor + ".dll"` (`BetterRuntime.cs:136`) | `libBetterRuntimeCPU.dylib` | CHANGE REQUIRED | wrapper seam `MacNativeRuntime.LibraryFileName` returns `libBetterRuntimeCPU.dylib` and resolves it by absolute path; the original literal cannot be reached, so the original resolver must return `IntPtr.Zero` instead of throwing (`BetterRuntime.cs:165`) for the wrapper resolver to be consulted |
| Resolver registration | `NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, ...)` in the `Native` static ctor (`BetterRuntime.cs:80-84`) | a resolver that yields the dylib | CHANGE REQUIRED | one resolver per assembly is allowed; the original already claims `BetterTranslator.Runtime`, so a second call throws. The wrapper sets its resolver on its own assemblies (`MacNativeRuntime.Install`) and additionally subscribes `AssemblyLoadContext.Default.ResolvingUnmanagedDll`. That event only fires when the primary resolver returns zero, so it is inert until the original stops throwing. Recorded as L4 in `blocked.md` |
| Search paths | `BackendCatalog.SearchPaths`, seeded `AppContext.BaseDirectory` (`RuntimeBackend.cs:65`), extended by `SearchAlso` (`:67-73`) | the macOS support folder must be probed | NO CHANGE | the wrapper calls the existing `BackendCatalog.SearchAlso` for `<Root>/runtime`, `<Root>/models` and `AppContext.BaseDirectory` |
| `runtime-support` carry folder (`RuntimeSupport.cs:28,33,49-81`) | copies MSVC redistributables beside the flavour | nothing to carry | CHANGE REQUIRED | `RuntimeSupport.VisualCppModules` must be empty off Windows, else `MissingBeside` reports `Incomplete` before any load (`RuntimeSupport.cs:101-104`) |
| Physical open | `LoadLibraryExW` + `AddDllDirectory`, both `kernel32.dll` (`RuntimeSupport.cs:141-145,153-174`) | `dlopen` through `NativeLibrary.Load` | CHANGE REQUIRED | guard `LoadScoped` with `OperatingSystem.IsWindows()` and fall back to `NativeLibrary.TryLoad(path)`; `AddDllDirectory` has no dlopen analogue, the dylib's own `@rpath` carries its dependencies |
| Failure text | `Marshal.GetHRForLastWin32Error`, HRESULT arms `0x8007007E` / `0x800700C1` (`RuntimeSupport.cs:24,26,127-135,169-171`) | `dlerror` text | CHANGE REQUIRED | on macOS surface the `DllNotFoundException` message from `NativeLibrary.TryLoad`; the two HRESULT arms are unreachable |
| RID plumbing | root `Directory.Build.props:11` pins `PlatformTarget x64` | `osx-arm64` | CHANGE REQUIRED | not editable under the freeze rule. `mac/Directory.Build.props` sets `RuntimeIdentifier osx-arm64` and does not import the root file; the engine projects are consumed by mirror projects under `mac/proj/`. Measured: a project-reference build stamps the assemblies PE machine `0x8664`, and the arm64 runtime then fails to load them |
| Flavour name table | `FileNameFor` returns three `.dll` names (`RuntimeBackend.cs:78-83`) | one CPU name | CHANGE REQUIRED | the wrapper never calls `FileNameFor`; `IInferenceBackendSeam` is the only producer of a backend in `mac/` |

## 2. Every DllImport in BetterRuntime.cs

All eighteen are `[DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]` with no `SetLastError`, no `CharSet`, no `ExactSpelling` and no `EntryPoint` override, so each entry point is the C# method name verbatim and case sensitive (`BetterRuntime.cs:198-259`). Cdecl is the only C convention on AAPCS64, so the attribute is a no-op there rather than a mismatch.

| Entry point | Return | Parameters | Same symbol in a CPU-only build | Verdict |
|---|---|---|---|---|
| `br_init` | `BrStatus` | none | yes | NO CHANGE |
| `br_shutdown` | `void` | none | yes | NO CHANGE |
| `br_version` | `IntPtr` | none | yes | NO CHANGE |
| `br_last_error` | `IntPtr` | none | yes | NO CHANGE |
| `br_model_params_default` | `void` | `ref ModelParams` | yes | NO CHANGE |
| `br_model_load` | `BrStatus` | `LPUTF8Str string`, `ref ModelParams`, `out IntPtr` | yes | NO CHANGE |
| `br_model_free` | `void` | `IntPtr` | yes | NO CHANGE |
| `br_model_n_ctx` | `int` | `IntPtr` | yes | NO CHANGE |
| `br_gen_params_default` | `void` | `ref GenParams` | yes | NO CHANGE |
| `br_build_translate_prompt` | `BrStatus` | `ref TranslateParamsNative`, `LPUTF8Str string`, `out IntPtr` | yes | NO CHANGE |
| `br_gen_start` | `BrStatus` | `IntPtr`, `LPUTF8Str string`, `ref GenParams`, `out IntPtr` | yes | NO CHANGE |
| `br_gen_start_translate` | `BrStatus` | `IntPtr`, `LPUTF8Str string`, `ref TranslateParamsNative`, `ref GenParams`, `out IntPtr` | yes | NO CHANGE |
| `br_gen_next` | `BrStatus` | `IntPtr`, `byte[]`, `int`, `out int`, `[I1] out bool` | yes | NO CHANGE |
| `br_gen_cancel` | `void` | `IntPtr` | yes | NO CHANGE |
| `br_gen_free` | `void` | `IntPtr` | yes | NO CHANGE |
| `br_complete` | `BrStatus` | `IntPtr`, `LPUTF8Str string`, `ref GenParams`, `out IntPtr` | yes | NO CHANGE |
| `br_translate` | `BrStatus` | `IntPtr`, `LPUTF8Str string`, `ref TranslateParamsNative`, `ref GenParams`, `out IntPtr` | yes | NO CHANGE |
| `br_string_free` | `void` | `IntPtr` | yes | NO CHANGE |
| `LoadLibraryExW`, `AddDllDirectory` (`RuntimeSupport.cs:141-145`) | `IntPtr` | `CharSet.Unicode`, `SetLastError = true` | no such symbol on macOS | CHANGE REQUIRED | guard both behind `OperatingSystem.IsWindows()`; see row 1.6 |

`BrStatus` is an `int`-backed enum (`BetterRuntime.cs:11-23`), which matches a C `int` return on AAPCS64. Verdict NO CHANGE.

## 3. Marshalling

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| `ModelParams` (`BetterRuntime.cs:43-49`) | `Sequential`, no `Pack`, `int NCtx`, `int NGpuLayers`, `int NThreads`, 12 bytes, blittable | same 12 bytes, 4-byte aligned | NO CHANGE | none. AAPCS64 aligns `int` on 4 like Win64 |
| `GenParams` (`:51-60`) | `Sequential`, no `Pack`, `int`, `float`, `float`, `int`, `float`, `uint`, 24 bytes, blittable | same | NO CHANGE | none |
| `TranslateParamsNative` (`:62-69`) | `Sequential`, four `LPUTF8Str` fields, not blittable, 32 bytes on LP64 | same four `char*` | NO CHANGE | none. The marshaller allocates and frees the UTF-8 buffers per call on both platforms |
| Struct passing | always by `ref`, never by value (`:211,215,224,228,232,237,251,256`) | `T*` on the C side | NO CHANGE | none. Passing by value would have differed: AAPCS64 returns and passes small aggregates differently from Win64 |
| `bool` width | one site, `[MarshalAs(UnmanagedType.I1)] out bool outDone` (`:241`) | 1-byte C `bool` | NO CHANGE | none. Without the attribute .NET would default to 4-byte `Win32 BOOL`; the attribute is what makes this correct on both |
| `char` width | no `char` field and no `CharSet` on any `br_` import | n/a | NO CHANGE | none |
| `size_t` against `nint` | no `size_t` parameter. Sizes cross as `int bufLen` and `out int outLen` (`:241`) | `int` stays `int` | NO CHANGE | none. A future `size_t` parameter would need `nuint` |
| UTF-8 strings in | `LPUTF8Str` on six parameters plus four struct fields (`:215,228,232,236,251,255,65-68`) | same | NO CHANGE | none |
| UTF-8 strings out, borrowed | `br_last_error`, `br_version` read with `Marshal.PtrToStringUTF8`, never freed (`:261,300`) | same | NO CHANGE | none, provided the CPU build keeps those pointers static |
| UTF-8 strings out, owned | `TakeString` frees with `br_string_free` in a `finally`, never `Marshal.FreeHGlobal` (`:273-287`) | same | NO CHANGE | none. Cross-allocator frees are as wrong on macOS as on Windows |
| Array ownership | `byte[] buf` marshalled as a pinned array for the call's duration (`:241`) | same | NO CHANGE | none. The buffer is not pinned across the `Task.Run` hop at `:464-468`, but each native call pins for its own duration |
| Pointer ownership | opaque `IntPtr` model and generation handles, freed by `br_model_free` and `br_gen_free` | same | NO CHANGE | none |

## 4. Callbacks

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| Function pointers in the ABI | none. All eighteen entry points take only `IntPtr`, `int`, `byte[]`, `ref`/`out` blittables and `LPUTF8Str` strings (`BetterRuntime.cs:198-259`) | n/a | NO CHANGE | none |
| Delegate lifetime and GC pinning | no delegate crosses the boundary; the only `UnmanagedFunctionPointer` in the tree is the Windows service dispatcher in `UpdateService/ServiceHost.cs:54,163,166`, unrelated to `br_` | n/a | NO CHANGE | none |
| Reverse P/Invoke | none, so no W^X or `pthread_jit_write_protect_np` concern on Apple silicon | n/a | NO CHANGE | none |
| Thread affinity of a raised callback | the runtime raises none. Cancellation is managed-side: `CancellationToken.Register` calls `br_gen_cancel` from the cancelling thread (`:390,456`), documented as the UI thread for Pause (`HostedSession.cs:345-347`) | `br_gen_cancel` must stay callable from a foreign thread | NO CHANGE | none in managed code. The CPU build must keep `br_gen_cancel` thread safe against a live `br_gen_next`, which is the same contract Windows relies on |

## 5. Threading and memory

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| Default thread count | `ModelParams.NThreads` is declared (`BetterRuntime.cs:48`) and never assigned anywhere in the tree, so it is always 0; `br_model_params_default` fills the struct and is then overwritten wholesale by `p = options.Value` (`:309-314`), so the runtime's own default does not survive either | on Apple silicon, hardware concurrency counts performance and efficiency cores together, and scheduling decode work onto E-cores costs throughput | CHANGE REQUIRED | set `NThreads` explicitly to the performance-core count for the CPU build, read from `sysctlbyname("hw.perflevel0.logicalcpu")`, rather than leaving 0 to mean `std::thread::hardware_concurrency()` |
| `NGpuLayers` | `0` for CPU, `-1` otherwise (`LocalTranslator.cs:160`) | always 0 | NO CHANGE | none in the original. The wrapper never selects a non-CPU backend, so the `-1` arm is unreachable from `mac/` |
| `NCtx` | 4096, deliberately not the model maximum (`LocalTranslator.cs:36,268`) | same | NO CHANGE | none |
| Thread pinning | none. No `ProcessorAffinity`, `Thread.Priority` or `PriorityClass` anywhere in `src/` | none, and pinning would fight the macOS QoS scheduler | NO CHANGE | none |
| Which threads call native | thread-pool threads for load, generate and the per-token `Task.Run` hop (`LocalTranslator.cs:296,485,645,787`; `BetterRuntime.cs:464-468`); the finalizer thread can call `br_model_free` (`:517-527`) | the CPU build must be free-threaded for these entry points | NO CHANGE | none in managed code |
| Serialisation | managed `SemaphoreSlim(1,1)` gate (`LocalTranslator.cs:40,173,360,623,716`); `br_gen_start` refuses a second live generation (`:358-359`) | same | NO CHANGE | none |
| `br_init` gating | once per process via `Interlocked.Exchange` (`BetterRuntime.cs:293,304-307`) | same | NO CHANGE | none |
| Memory mapping | no `MemoryMappedFile` anywhere in the repo | the native side mmaps the gguf itself | NO CHANGE | none in managed code |
| 16 KB page size | no managed page-size assumption; no `Environment.SystemPageSize` in the tree | the native CPU build must not assume 4 KB pages for its mmap or arena alignment | NEW | build the arm64 artifact with the 16 KB page assumption and link with `-Wl,-segalign,0x4000`; there is nothing to change on the managed side |
| Cache line size | no managed assumption; no `cacheline` or `cache_line` in the tree | Apple silicon has a 128-byte line against 64 on x86-64 | NEW | any false-sharing padding in the native build must be 128 bytes; managed code is unaffected |
| `br_gen_next` buffer | `new byte[1024]`, regrown as `new byte[len + 1]` on `BufferTooSmall`, the same token re-handed rather than consumed (`:397,403-408,460,470-473`) | same | NO CHANGE | none |
| Handle lifetime ordering | `reg.Dispose()` before `br_gen_free` in `CompleteCounted` (`:432-439`) so a cancel cannot reach a freed pointer | same | NO CHANGE | none. `TranslateStream` disposes its `using` registration after the `finally` that frees (`:456,495-498`), which is the same ordering hazard on both platforms and is not macOS specific |
| File locking | `FileShare.None` on download targets (`DownloadManager.cs:243`) and `FileOptions.DeleteOnClose` on the artifact lock (`ArtifactLock.cs:63-78`) | advisory only on macOS | CHANGE REQUIRED | the artifact lock must not rely on mandatory locking to exclude a second writer; add an explicit lock-file protocol, or accept that concurrent installs are excluded only by the single-instance seam. The wrapper's `MacSingleInstance` takes an exclusive `FileShare.None` handle for that reason |
| Named pipes | `NamedPipeServerStream` for the inference host (`HostedSession.cs:89-94`) and the single-instance channel | .NET maps named pipes to Unix domain sockets under `TMPDIR` on macOS | NO CHANGE | none. The wrapper reuses the same API for `MacSingleInstance` |

## 6. Build flags for the native CPU-only artifact

Building the artifact is out of scope for this run. These are the flags the CPU-only osx-arm64 build must carry.

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| GPU backends | separate `BetterRuntimeCUDA.dll` and `BetterRuntimeVulkan.dll` artifacts (`ComponentCatalog.cs:129,140,152`) | none built at all | NEW | configure with `-DGGML_CUDA=OFF -DGGML_VULKAN=OFF -DGGML_METAL=OFF -DGGML_BLAS=OFF` unless the Accelerate row below is taken |
| Target triple | MSVC `x64` (`BetterTranslator.Bootstrap.vcxproj:4-13`) | `arm64-apple-macos` | NEW | `-DCMAKE_OSX_ARCHITECTURES=arm64 -DCMAKE_OSX_DEPLOYMENT_TARGET=13.0` |
| CPU SIMD path | AVX2 on x86-64 | NEON, plus the ARMv8.2 dot-product and fp16 extensions that Apple silicon has | NEW | `-DGGML_NATIVE=OFF` with explicit `-march=armv8.2-a+dotprod+fp16` so the artifact is reproducible rather than tied to the build host |
| Accelerated BLAS | none carried on the CPU flavour; cuBLAS belongs to the CUDA flavour only (`RuntimeBackend.cs:100`) | Apple's Accelerate framework is the platform BLAS | NEW | `-DGGML_BLAS=ON -DGGML_BLAS_VENDOR=Apple` and link `-framework Accelerate`. Accelerate is a CPU path (AMX and NEON), not a GPU one |
| Output name and install name | `BetterRuntimeCPU.dll` | `libBetterRuntimeCPU.dylib` with a self-contained install name | NEW | `-DCMAKE_SHARED_LIBRARY_PREFIX=lib`, `-install_name @rpath/libBetterRuntimeCPU.dylib`, and `-Wl,-rpath,@loader_path` so sibling dylibs resolve without `DYLD_LIBRARY_PATH` |
| Symbol visibility | all eighteen `br_` symbols exported | same, and no C++ mangling | NEW | keep `extern "C"` and `-fvisibility=hidden` with explicit `__attribute__((visibility("default")))` on the `br_` entry points |
| Hardened runtime | not applicable | notarisation requires it | NEW | `-Wl,-headerpad_max_install_names`, then sign with the hardened runtime. Out of scope here, listed in `deferred-macos.md` |

## 7. Paths and encoding

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| Separator construction | `Path.Combine` throughout the model and runtime paths (`InstallPaths.cs:61,63`; `ModelLibrary.cs:57`) | `/` | NO CHANGE | none. No drive letter and no backslash literal appears on the model or runtime path |
| gguf path to the ABI | built with `Path.Combine`, handed to `br_model_load` as `LPUTF8Str` (`InstallPaths.cs:61`; `BetterRuntime.cs:214-215,316`) | same | NO CHANGE | none |
| Case sensitivity, loaded-model identity | loaded path compared `OrdinalIgnoreCase` (`LocalTranslator.cs:181`) | APFS is case insensitive by default but can be formatted case sensitive | CHANGE REQUIRED | two files differing only in case are one model to this comparison. On a case-sensitive volume that is wrong. Compare ordinally on macOS, or canonicalise through `Path.GetFullPath` first |
| Case sensitivity, model scan | dictionary keyed `OrdinalIgnoreCase` and name matches `OrdinalIgnoreCase` (`ModelLibrary.cs:48,59,90-91`; `ModelSelection.cs:76`; `ModelResolver.cs:163-164`) | same caveat | CHANGE REQUIRED | same canonicalisation |
| Glob case | `Directory.EnumerateFiles(folder, "*.gguf", AllDirectories)` (`ModelLibrary.cs:57`) | `Directory.EnumerateFiles` matching is case sensitive on Unix, so `*.GGUF` is invisible | CHANGE REQUIRED | enumerate `"*"` and filter the extension with `OrdinalIgnoreCase` |
| Unicode normalization | none. No `.Normalize(` and no `NormalizationForm` anywhere in `src/` | APFS returns NFD-decomposed names while a C# literal is NFC, and every comparison is ordinal | CHANGE REQUIRED | normalize both sides to NFC before any ordinal path comparison, or compare through `FileInfo`/`Path.GetFullPath` |
| Application data root | `SpecialFolder.LocalApplicationData` + `BetterTranslator` (`AppPaths.cs:16-18`) | `~/Library/Application Support/BetterTranslator` | NO CHANGE | measured on this host with .NET 10.0.400: `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` returns `/Users/<user>/Library/Application Support`, so `AppPaths.DefaultRoot` already lands on `~/Library/Application Support/BetterTranslator`. `XDG_DATA_HOME` is ignored on macOS. The wrapper still routes every path through `MacAppPaths`, which constructs `new AppPaths(root)` on the existing public constructor and adds `runtime`, `cache` and `logs` beneath the same root, so nothing is written inside an application bundle |
| Host executable | `Path.Combine(AppContext.BaseDirectory, "BetterTranslator.Host.exe")`, `.exe` hardcoded (`HostedSession.cs:69-70`) | an extensionless Mach-O executable | CHANGE REQUIRED | the `File.Exists` gate at `HostedSession.cs:72` fails on macOS, so `LocalTranslator` silently runs in process (`LocalTranslator.cs:266-305`). That fallback is correct behaviour, not a crash, but the out-of-process isolation is lost. Make the file name platform dependent |
| Legacy HTTP runtime | `<ModelsFolder>/betterruntime/betterruntime.exe` (`InferenceHost.cs:44`) | same rename | CHANGE REQUIRED | same treatment. Reached only by the legacy embedding path |

## 8. Model and quantization support

| Item | Windows today | CPU-only osx-arm64 needs | Verdict | Concrete change |
|---|---|---|---|---|
| Model files in the catalogue | `EuroLLM-9B-Instruct-Q4_K_M.gguf` (`ComponentCatalog.cs:31-33`), `translategemma-4b-it-Q4_K_M.gguf` (`:51-53`) | same files | NO CHANGE | none. Both are CPU-loadable gguf |
| Manifest model rows | `EuroLLM-9B-Instruct-Q3_K_L`, `EuroLLM-9B-Instruct-Q4_K_M`, `gpt-oss-20b-MXFP4`, `translategemma-4b-it-Q4_K_M`, `translategemma-4b-it.mmproj-f16`, `translategemma-4b-it.Q4_K_M`, `translategemma-4b-it.Q4_K_S`, `translategemma-latest-Q4_K_M`, `gemma-4-26B-A4B-it-QAT-Q4_0`, `gemma3-1b-Q4_K_M`, `mmproj-gemma-4-26B-A4B-it-QAT-BF16` (`ArtifactManifest.cs:91-101`) | same | NO CHANGE | none |
| Quantization formats | `Q3_K_L`, `Q4_K_M`, `Q4_K_S`, `Q4_0`, `MXFP4`, `f16`, `BF16` | all are CPU kernels in llama.cpp | NO CHANGE | none. `Q4_0` additionally has an ARM dot-product kernel, which is why the `+dotprod` flag above matters |
| Quant formats depending on a GPU backend | none in the catalogue or the manifest | n/a | NO CHANGE | none. No listed quant requires a GPU backend |
| gguf integrity check | magic `47 47 55 46` and version 3 (`ArtifactManifest.cs:191-216`) | same | NO CHANGE | none, the check is byte-level and platform independent |
| Runtime artifact integrity check | Windows PE magic `4D 5A` required for anything ending `.dll` (`ArtifactManifest.cs:139-152,172-189`) | Mach-O `CF FA ED FE` for a 64-bit little-endian arm64 dylib | CHANGE REQUIRED | gate the magic check on the artifact's extension and accept Mach-O for `.dylib`; a dylib presented today would be rejected as corrupt |
| Runtime artifact rows | `BetterRuntimeCPU.dll` 3595264 bytes plus two GPU rows (`ArtifactManifest.cs:60,61,67`) | one `libBetterRuntimeCPU.dylib` row with its own size and sha256 | NEW | add the macOS row once the artifact is built. The wrapper does not add it: no size or hash may be invented |
| Companion artifacts | `cublas64_13.dll`, `cublasLt64_13.dll` (`ArtifactManifest.cs:80,87`; `ComponentCatalog.cs:180-192`) | none | NO CHANGE | none in the original. `DependenciesFor` returns an empty list for CPU (`RuntimeBackend.cs:100-102`), so the companion loop in `DownloadManager.cs:51-88` is already inert for a CPU-only wrapper |
