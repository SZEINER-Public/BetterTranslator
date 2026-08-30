namespace BetterTranslator.Core.Models;

public enum ChatScope
{
    WholeProject,
    ThisChat,
}

public sealed class AppSettings
{
    public bool LearnFromMyEdits { get; set; } = true;

    public bool UnderlineMemoryWords { get; set; } = true;

    public bool ReindexFilesWhenTheyChange { get; set; } = true;

    public ChatScope DefaultScopeForNewChats { get; set; } = ChatScope.WholeProject;

    /// <summary>
    /// Whole percent. Anything the engine is less certain about than this gets
    /// a dotted underline and shows up under Unsure.
    /// </summary>
    public int UnsureThresholdPercent { get; set; } = 80;

    public string TargetLanguage { get; set; } = "Czech";

    /// <summary>
    /// Which runtime flavour to load. Only takes effect at startup: Windows
    /// will not swap a native library that is already loaded.
    /// </summary>
    public RuntimeBackend RuntimeBackend { get; set; } = RuntimeBackend.Cpu;

    /// <summary>
    /// Full path to the GGUF to translate with. Empty until one is chosen or
    /// one is found on disk, and re-resolved when the file has gone.
    ///
    /// The effort outranks this. Simple and Thinking are two names for two models
    /// and sit on the composer where they are read every send, so letting a
    /// buried picker quietly win would make that visible control do nothing.
    /// This is what runs when the chosen effort's own model is not installed.
    /// </summary>
    public string SelectedModelPath { get; set; } = string.Empty;

    /// <summary>
    /// Simple or Thinking. Kept across sessions because it selects the model that
    /// gets loaded, and reloading one costs seconds and gigabytes -- landing on
    /// the wrong one at launch is not a cheap mistake.
    /// </summary>
    public TranslationEffort Effort { get; set; } = EffortTiers.Default;

    /// <summary>
    /// Apply the software-domain vocabulary to every send. On by default: the
    /// project glossary waits for the Memory chip because it is a claim about
    /// one body of documents, while this is what the words of the trade mean in
    /// the target language at all.
    /// </summary>
    public bool UseDomainVocabulary { get; set; } = true;

    /// <summary>
    /// Sampling temperature, from Advanced. Low keeps wording close to the
    /// source.
    ///
    /// Stored, unlike most of what Advanced shows, because it is not only the
    /// window's: an agent translating through this application has to send what
    /// the reader chose, and it cannot read a value that lives in a view model
    /// until the process closes.
    /// </summary>
    public double Temperature { get; set; } = 0.2;

    /// <summary>
    /// The standing instruction from Advanced, sent with every translation.
    /// Empty when unset, which is the normal case. Stored for the same reason as
    /// <see cref="Temperature"/>.
    /// </summary>
    public string Instruction { get; set; } = string.Empty;

    public Verification.VerificationSettings Verification { get; set; } = new();

    /// <summary>
    /// Close and open again once an install finishes, so the runtime flavour or
    /// the model that was just written is the one that loads. On by default:
    /// a component installed into a running process is not the component the
    /// process is using.
    /// </summary>
    public bool RestartAfterInstall { get; set; } = true;

    public bool McpEnabled { get; set; }

    public string McpHost { get; set; } = "127.0.0.1";

    public int McpPort { get; set; } = 8765;

    public string McpToken { get; set; } = string.Empty;
}
