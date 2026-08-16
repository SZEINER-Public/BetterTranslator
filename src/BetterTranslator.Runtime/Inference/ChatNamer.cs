using System.Collections.Concurrent;
using BetterTranslator.Engine.Chats;
using BetterTranslator.Engine.Models;

namespace BetterTranslator.Runtime.Inference;

/// <summary>What is known about a chat when it is time to name it.</summary>
public sealed record ChatNameRequest
{
    public required string Source { get; init; }

    public required string ModelPath { get; init; }

    public required Core.Languages.TranslationDirection Direction { get; init; }

    public Language From => new(Direction.Source.Code.Value, Direction.Source.Name);

    public Language To => new(Direction.Target.Code.Value, Direction.Target.Name);

    /// <summary>The truncated name the chat is carrying, and the fallback to translate.</summary>
    public required string Provisional { get; init; }
}

/// <summary>
/// Names a chat with the model that just translated its first message.
///
/// Three tiers, because the installed models are translation fine-tunes and a
/// summarize instruction is not what they were trained on. Ask for a title;
/// failing that translate the truncated name the chat already has, which is a
/// translation model's native competence and still leaves the sidebar reading in
/// the language the reader chose; failing that keep the truncation and say
/// nothing. Naming a chat is a convenience, so every failure is silent.
/// </summary>
/// <param name="complete">(modelPath, prompt, maxTokens) -> raw model text.</param>
/// <param name="translate">The ordinary guarded translate path, for tier two.</param>
/// <param name="template">Defaults to reading the model's own declared template.</param>
public sealed class ChatNamer(
    Func<string, string, int, CancellationToken, Task<string?>> complete,
    Func<TranslationJob, CancellationToken, Task<TranslationOutcome>> translate,
    Func<string, ChatTemplate?>? template = null)
{
    private readonly Func<string, ChatTemplate?> _template = template ?? TemplateFor;

    public ChatNamer(LocalTranslator translator)
        : this(translator.CompleteAsync, translator.TranslateAsync)
    {
    }

    /// <summary>Five words needs far less, and the surplus is room for a label the validator strips.</summary>
    private const int TitleTokens = 64;

    private static readonly ConcurrentDictionary<string, ChatTemplate?> Templates = new(StringComparer.OrdinalIgnoreCase);

    public async Task<string?> NameAsync(ChatNameRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            var summarised = await SummariseAsync(request, cancellationToken).ConfigureAwait(false);

            return summarised
                ?? await TranslateProvisionalAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is BetterRuntimeException or IOException)
        {
            // A name is a convenience. The runtime crashing while producing one,
            // or the model file being unreadable, is the caller's cue to keep the
            // truncation rather than something to raise.
            return null;
        }
    }

    private async Task<string?> SummariseAsync(ChatNameRequest request, CancellationToken cancellationToken)
    {
        var prompt = TitlePromptBuilder.Build(request.Source, request.To.Name, _template(request.ModelPath));

        if (prompt is null)
        {
            return null;
        }

        var answer = await complete(request.ModelPath, prompt, TitleTokens, cancellationToken)
            .ConfigureAwait(false);

        return Settle(ChatTitle.Clean(answer, request.Source), request);
    }

    private async Task<string?> TranslateProvisionalAsync(ChatNameRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Provisional))
        {
            return null;
        }

        var job = new TranslationJob
        {
            Text = request.Provisional,
            ModelPath = request.ModelPath,
            Direction = request.Direction,
            IsStandalone = true,
        };

        var outcome = await translate(job, cancellationToken).ConfigureAwait(false);

        return Settle(ChatTitle.Clean(outcome.Text, request.Provisional), request);
    }

    /// <summary>
    /// The domain vocabulary applies to a title as much as to a translation: a
    /// chat about the project's Engine must not be filed under the Czech word
    /// for the machine in a car. The translation guards do not, and are not run.
    /// </summary>
    private static string? Settle(string? title, ChatNameRequest request)
    {
        if (title is null)
        {
            return null;
        }

        var corrected = Engine.Terminology.TerminologyCorrector
            .Apply(request.Source, title, Engine.Terminology.DomainTerms.For(request.To.Code))
            .Text;

        return string.IsNullOrWhiteSpace(corrected) ? null : corrected;
    }

    /// <summary>
    /// Cached: reading it opens the GGUF and walks its header, which is fine once
    /// per translation and not fine once per chat.
    /// </summary>
    private static ChatTemplate? TemplateFor(string modelPath) =>
        Templates.GetOrAdd(modelPath, ChatTemplate.For);
}
