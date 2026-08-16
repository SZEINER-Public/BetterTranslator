using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BetterTranslator.Runtime;

public enum BrStatus
{
    Ok = 0,
    InvalidArg = -1,
    ModelLoad = -2,
    Context = -3,
    Decode = -4,
    Template = -5,
    Cancelled = -6,
    BufferTooSmall = -7,
    OutOfMemory = -8,
    Internal = -99,
}

public sealed class BetterRuntimeException : Exception
{
    public BrStatus Status { get; }

    public BetterRuntimeException(BrStatus status, string detail)
        : base($"BetterRuntime error {status}: {detail}")
        => Status = status;

    /// <summary>
    /// A failure that did not come from a native status code -- the inference
    /// host failing to start, or dying mid-generation. The message is what a
    /// reader is shown, so it carries no error-code preamble.
    /// </summary>
    public BetterRuntimeException(string message)
        : base(message)
        => Status = BrStatus.Internal;
}

[StructLayout(LayoutKind.Sequential)]
public struct ModelParams
{
    public int NCtx;         // 0 = 4096. Do not set this to the model maximum.
    public int NGpuLayers;   // 0 = CPU only, -1 = offload all
    public int NThreads;     // 0 = hardware concurrency
}

[StructLayout(LayoutKind.Sequential)]
public struct GenParams
{
    public int MaxTokens;
    public float Temperature;
    public float TopP;
    public int TopK;
    public float RepeatPenalty;
    public uint Seed;
}

[StructLayout(LayoutKind.Sequential)]
internal struct TranslateParamsNative
{
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string SourceLangCode;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string SourceLangName;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TargetLangCode;
    [MarshalAs(UnmanagedType.LPUTF8Str)] public string TargetLangName;
}

public readonly record struct Language(string Code, string Name);

internal static class Native
{
    // Logical name. The resolver below maps it to the flavor that suits the machine, so the
    // physical files can be BetterRuntimeCPU.dll / BetterRuntimeVulkan.dll / BetterRuntimeCUDA.dll
    // without any of these declarations changing.
    private const string Lib = "BetterRuntime";

    static Native()
    {
        NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, asm, path) =>
            name == Lib ? NativeLibrary.Load(ResolveFlavor(), asm, path) : IntPtr.Zero);
    }

    // Set from the stored setting before the first native call. Windows will not
    // unload and swap a loaded DLL, so this is read once, at first use, and a
    // change only takes effect on the next start -- which is what the settings
    // surface tells the user.
    internal static string? PreferredFlavor { get; set; }

    /// <summary>
    /// The flavour that actually loaded, which is not always the one asked for.
    /// Null until the first native call resolves one.
    /// </summary>
    internal static string? LoadedFlavor { get; private set; }

    /// <summary>
    /// Why the chosen flavour was not the one used, or null when it was.
    ///
    /// This exists because its absence cost a day. A GPU flavour that is present
    /// but cannot load -- CUDA without the cuBLAS libraries beside it is the
    /// measured case -- failed `TryLoad` exactly as a missing file does, and the
    /// loop moved quietly on to the CPU build. The settings screen still said
    /// CUDA, because it asks `File.Exists`, and the only visible symptom was that
    /// generation ran at processor speed.
    /// </summary>
    internal static string? Substitution { get; private set; }

    private static string ResolveFlavor()
    {
        var dirs = Inference.BackendCatalog.SearchPaths.Count > 0
            ? Inference.BackendCatalog.SearchPaths
            : [Path.GetDirectoryName(typeof(Native).Assembly.Location) ?? AppContext.BaseDirectory];

        // The chosen flavor first, then the standard order as a fallback, so a
        // stale choice degrades to something that runs instead of failing.
        string[] order = PreferredFlavor is { Length: > 0 } chosen
            ? [chosen, "BetterRuntimeCUDA", "BetterRuntimeVulkan", "BetterRuntimeCPU"]
            : ["BetterRuntimeCUDA", "BetterRuntimeVulkan", "BetterRuntimeCPU"];

        // Kept per flavour rather than as one string: the reason the chosen
        // flavour was refused is the one worth reporting, and it is not
        // necessarily the last failure seen.
        var refusals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var flavor in order)
        {
            foreach (var dir in dirs)
            {
                var candidate = Path.Combine(dir, flavor + ".dll");

                if (!File.Exists(candidate))
                {
                    continue;
                }

                // Load rather than TryLoad, for the message. TryLoad collapses
                // "not there" and "there but its dependencies are not" into the
                // same false, and those are the two cases that must not read
                // alike.
                try
                {
                    var handle = NativeLibrary.Load(candidate);
                    NativeLibrary.Free(handle);

                    LoadedFlavor = flavor;
                    Substitution = Explain(PreferredFlavor, flavor, refusals);

                    return candidate;
                }
                catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException)
                {
                    refusals[flavor] = ex.Message;
                }
            }
        }

        var detail = refusals.Count > 0
            ? " Found but could not load: " + string.Join("; ", refusals.Select(r => $"{r.Key}.dll -- {r.Value}"))
            : string.Empty;

        throw new DllNotFoundException(
            $"No BetterRuntime flavor found in {string.Join(" or ", dirs)}. Expected BetterRuntimeCPU.dll or a GPU flavor.{detail}");
    }

    /// <summary>
    /// One sentence naming what was asked for, what ran instead, and why -- in
    /// that order, because the reader chose the first and is about to wonder
    /// about the second.
    /// </summary>
    private static string? Explain(string? wanted, string loaded, Dictionary<string, string> refusals)
    {
        if (wanted is null or { Length: 0 } || wanted.Equals(loaded, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!refusals.TryGetValue(wanted, out var reason))
        {
            // Asked for, never found on disk. A different sentence from present
            // and broken, and the installer is the answer to this one.
            return $"{wanted}.dll was chosen but is not installed, so {loaded}.dll is running instead.";
        }

        // Named rather than left to the OS wording. "The specified module could
        // not be found" about a DLL that is plainly on disk is about a
        // dependency, and saying which one is the difference between a fix and a
        // reinstall.
        var missing = wanted.Contains("CUDA", StringComparison.OrdinalIgnoreCase)
            ? " The CUDA build needs cublas64_13.dll and cublasLt64_13.dll beside it; these ship with the runtime, not with the NVIDIA driver."
            : string.Empty;

        return $"{wanted}.dll is installed but could not be loaded, so {loaded}.dll is running instead.{missing} ({reason})";
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_init();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_shutdown();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr br_version();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr br_last_error();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_model_params_default(ref ModelParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_model_load(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string ggufPath, ref ModelParams p, out IntPtr model);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_model_free(IntPtr model);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int br_model_n_ctx(IntPtr model);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_gen_params_default(ref GenParams p);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_build_translate_prompt(
        ref TranslateParamsNative tp, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, out IntPtr result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_gen_start(
        IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, ref GenParams p, out IntPtr gen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_gen_start_translate(
        IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        ref TranslateParamsNative tp, ref GenParams gp, out IntPtr gen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_gen_next(
        IntPtr gen, byte[] buf, int bufLen, out int outLen, [MarshalAs(UnmanagedType.I1)] out bool outDone);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_gen_cancel(IntPtr gen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_gen_free(IntPtr gen);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_complete(
        IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string prompt, ref GenParams p, out IntPtr result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern BrStatus br_translate(
        IntPtr model, [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
        ref TranslateParamsNative tp, ref GenParams gp, out IntPtr result);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void br_string_free(IntPtr s);

    internal static string LastError() => Marshal.PtrToStringUTF8(br_last_error()) ?? "";

    internal static void Check(BrStatus st)
    {
        if (st != BrStatus.Ok)
        {
            throw new BetterRuntimeException(st, LastError());
        }
    }

    // Takes ownership of a char* returned by the DLL and frees it with the DLL's allocator.
    // Never use Marshal.FreeHGlobal here: the runtimes use different heaps.
    internal static string TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero)
        {
            return "";
        }
        try
        {
            return Marshal.PtrToStringUTF8(p) ?? "";
        }
        finally
        {
            br_string_free(p);
        }
    }
}

public sealed class BetterRuntimeModel : IDisposable
{
    private IntPtr _handle;
    private static int _initialized;

    public int ContextSize { get; }

    // For tests that must drive the raw ABI to prove a contract the wrapper hides.
    internal IntPtr Handle => _handle;

    public static string Version => Marshal.PtrToStringUTF8(Native.br_version()) ?? "unknown";

    public BetterRuntimeModel(string ggufPath, ModelParams? options = null)
    {
        if (Interlocked.Exchange(ref _initialized, 1) == 0)
        {
            Native.Check(Native.br_init());
        }

        var p = new ModelParams();
        Native.br_model_params_default(ref p);
        if (options.HasValue)
        {
            p = options.Value;
        }

        Native.Check(Native.br_model_load(ggufPath, ref p, out _handle));
        ContextSize = Native.br_model_n_ctx(_handle);
    }

    public static GenParams DefaultGenParams()
    {
        var p = new GenParams();
        Native.br_gen_params_default(ref p);
        return p;
    }

    /// <summary>Returns the exact prompt the runtime would send, without running the model.</summary>
    public string BuildTranslatePrompt(string text, Language from, Language to)
    {
        var tp = MakeParams(from, to);
        Native.Check(Native.br_build_translate_prompt(ref tp, text, out var result));
        return Native.TakeString(result);
    }

    /// <summary>One-shot translation. Blocks; call from a background thread.</summary>
    public string Translate(string text, Language from, Language to, GenParams? gen = null)
    {
        ThrowIfDisposed();
        var tp = MakeParams(from, to);
        var gp = gen ?? DefaultGenParams();
        Native.Check(Native.br_translate(_handle, text, ref tp, ref gp, out var result));
        return Native.TakeString(result);
    }

    public Task<string> TranslateAsync(string text, Language from, Language to,
                                       GenParams? gen = null, CancellationToken ct = default)
        => Task.Run(() => Translate(text, from, to, gen), ct);

    /// <summary>
    /// Runs a prompt as given. The runtime applies no chat template here, so the
    /// prompt has to arrive already carrying its turn markers -- measured: the
    /// same question sent bare came back as a raw continuation, and sent inside
    /// markers came back answered. Pair this with BuildTranslatePrompt rather
    /// than writing a template by hand.
    /// </summary>
    public string Complete(string prompt, GenParams? gen = null)
    {
        ThrowIfDisposed();
        var gp = gen ?? DefaultGenParams();
        Native.Check(Native.br_complete(_handle, prompt, ref gp, out var result));
        return Native.TakeString(result);
    }

    public Task<string> CompleteAsync(string prompt, GenParams? gen = null, CancellationToken ct = default)
        => Task.Run(() => Complete(prompt, gen), ct);

    /// <summary>
    /// The same as <see cref="Complete"/>, but driven a step at a time so the
    /// tokens can be counted.
    ///
    /// This is the only exact token figure the runtime can give: the ABI exports
    /// no tokenizer -- verified against the DLL, eighteen br_* symbols and not
    /// one of them counts or splits text -- so the prompt side cannot be
    /// measured at all, and generating is the only place where a count falls out
    /// of work that is happening anyway. Each br_gen_next call decodes one
    /// token, which is what makes the tally real rather than an estimate from
    /// character length.
    ///
    /// A step that returns no bytes still counts. It decoded a token whose UTF-8
    /// sequence is not finished yet, and skipping those would under-report every
    /// answer carrying diacritics.
    /// </summary>
    public (string Text, int Tokens) CompleteCounted(string prompt, GenParams? gen = null, CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var gp = gen ?? DefaultGenParams();
        Native.Check(Native.br_gen_start(_handle, prompt, ref gp, out var handle));

        var reg = ct.Register(() => Native.br_gen_cancel(handle));

        var text = new System.Text.StringBuilder();
        var tokens = 0;

        try
        {
            var buf = new byte[1024];

            while (true)
            {
                var status = Native.br_gen_next(handle, buf, buf.Length, out var len, out var done);

                if (status == BrStatus.BufferTooSmall)
                {
                    // Not a step: the same token is handed back on the next call.
                    buf = new byte[len + 1];
                    continue;
                }

                if (status == BrStatus.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                    break;
                }

                Native.Check(status);

                if (len > 0)
                {
                    text.Append(System.Text.Encoding.UTF8.GetString(buf, 0, len));
                }

                if (done)
                {
                    break;
                }

                tokens++;
            }
        }
        finally
        {
            // Before the handle is freed, not after. The registration calls
            // br_gen_cancel on it, and a cancel arriving between the free and
            // the automatic unregister would hand the runtime a pointer it has
            // already given back.
            reg.Dispose();
            Native.br_gen_free(handle);
        }

        return (text.ToString(), tokens);
    }

    /// <summary>Streams translated text as it is generated. Honours cancellation mid-generation.</summary>
    public async IAsyncEnumerable<string> TranslateStream(
        string text, Language from, Language to, GenParams? gen = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ThrowIfDisposed();

        var tp = MakeParams(from, to);
        var gp = gen ?? DefaultGenParams();

        Native.Check(Native.br_gen_start_translate(_handle, text, ref tp, ref gp, out var handle));

        using var reg = ct.Register(() => Native.br_gen_cancel(handle));

        try
        {
            var buf = new byte[1024];

            while (true)
            {
                var (status, len, done) = await Task.Run(() =>
                {
                    var st = Native.br_gen_next(handle, buf, buf.Length, out var n, out var d);
                    return (st, n, d);
                }, CancellationToken.None).ConfigureAwait(false);

                if (status == BrStatus.BufferTooSmall)
                {
                    buf = new byte[len + 1];
                    continue;
                }

                if (status == BrStatus.Cancelled)
                {
                    ct.ThrowIfCancellationRequested();
                    yield break;
                }

                Native.Check(status);

                if (len > 0)
                {
                    yield return System.Text.Encoding.UTF8.GetString(buf, 0, len);
                }

                if (done)
                {
                    yield break;
                }
            }
        }
        finally
        {
            Native.br_gen_free(handle);
        }
    }

    private static TranslateParamsNative MakeParams(Language from, Language to) => new()
    {
        SourceLangCode = from.Code,
        SourceLangName = from.Name,
        TargetLangCode = to.Code,
        TargetLangName = to.Name,
    };

    private void ThrowIfDisposed()
    {
        if (_handle == IntPtr.Zero)
        {
            throw new ObjectDisposedException(nameof(BetterRuntimeModel));
        }
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            Native.br_model_free(_handle);
            _handle = IntPtr.Zero;
        }
        GC.SuppressFinalize(this);
    }

    ~BetterRuntimeModel() => Dispose();
}
