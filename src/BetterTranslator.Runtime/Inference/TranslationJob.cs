using BetterTranslator.Core.Models;

namespace BetterTranslator.Runtime.Inference;

/// <summary>
/// What one translation produced, and what it cost.
///
/// The cost is deliberately only what can be measured. Generated tokens are a
/// real count of decode steps; the prompt side is absent because the runtime
/// exports no tokenizer, and a figure estimated from character length sitting
/// beside a measured one would be indistinguishable from it.
/// </summary>
public sealed record TranslationOutcome(string? Text, int GeneratedTokens, TimeSpan Duration)
{
    public Core.Verification.VerificationResult? Verification { get; init; }

    /// <summary>
    /// Why the pair itself stopped the run, when it did. Typed rather than
    /// worded, so the caller owns the sentence and the engine owns the fact.
    /// </summary>
    public Core.Languages.DirectionVerdict? Direction { get; init; }

    /// <summary>
    /// Nothing came back. Still carries the elapsed time, because a failure that
    /// took forty seconds is worth telling apart from one that failed at once.
    /// </summary>
    public static TranslationOutcome None(TimeSpan elapsed) => new(null, 0, elapsed);

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public TranslationOutcome CountingReverseCalls()
    {
        if (Verification?.Gate?.Escalation is not { ReverseCalls: > 0 } escalation)
        {
            return this;
        }

        return this with
        {
            GeneratedTokens = GeneratedTokens + escalation.ReverseTokens,
            Duration = Duration + TimeSpan.FromMilliseconds(escalation.ReverseDurationMs),
        };
    }

    /// <summary>
    /// Tokens per second, or null when there is nothing to divide. Reported
    /// rather than the raw pair alone: it is the figure that says whether the
    /// chosen backend is actually doing the work.
    /// </summary>
    public double? TokensPerSecond =>
        GeneratedTokens > 0 && Duration.TotalSeconds > 0
            ? GeneratedTokens / Duration.TotalSeconds
            : null;
}

/// <summary>
/// One translation, with everything that decides how it runs. A record rather
/// than a parameter list because the grounded and free paths differ only by
/// which of these fields are set, and a five-argument call could not say that.
/// </summary>
public sealed record TranslationJob
{
    public required string Text { get; init; }

    public required string ModelPath { get; init; }

    /// <summary>
    /// The pair the user chose, canonicalized once where they chose it. It is
    /// the only place either side of a translation is decided: nothing below
    /// this line reads a language from a control, a label or a default.
    /// </summary>
    public required Core.Languages.TranslationDirection Direction { get; init; }

    public Language From => new(Direction.Source.Code.Value, Direction.Source.Name);

    public Language To => new(Direction.Target.Code.Value, Direction.Target.Name);

    public TranslationEffort Effort { get; init; } = TranslationEffort.Simple;

    public float Temperature { get; init; } = 0.2f;

    /// <summary>
    /// The standing instruction from Advanced, sent with every request. Null
    /// when the field is empty.
    /// </summary>
    public string? Instruction { get; init; }

    /// <summary>
    /// Retrieved context. Null is the normal case and means the model sees this
    /// text and nothing else -- no history, no project, no earlier turn. It is
    /// only non-null when the composer's Memory chip is attached.
    /// </summary>
    public string? Memory { get; init; }

    /// <summary>
    /// Apply the project's own vocabulary: its glossary of required term
    /// renderings, and its list of words that may not appear unless the source
    /// justifies them.
    ///
    /// OFF unless a project or folder is in scope, and that default is the
    /// important part. A glossary is a statement about ONE body of documents --
    /// the shipped one maps "store" to "úložiště", which is right for a sentence
    /// about a model store and wrong for "Mr. White went to the store last
    /// night". Applied to general prose it rejects correct translations, and
    /// because a refusal keeps the source, the user sees untranslated English
    /// with no explanation.
    /// </summary>
    public bool UseProjectVocabulary { get; init; }

    /// <summary>
    /// This text is a unit a person typed -- a line or a sentence of a chat
    /// message -- rather than a chunk of a document.
    ///
    /// It relaxes two gates, and only those two. Both were calibrated over 503
    /// document lines and both misfire on short standalone prose, measured on a
    /// real message: an eight-word sentence sits one word past a proportional
    /// ceiling that a seven-word sentence clears by seven, and a sentence whose
    /// Czech spells an abbreviation out in brackets is refused for bracket
    /// damage. Neither failure can corrupt anything here -- there is no document
    /// structure around a chat sentence to lose -- and both cost the reader the
    /// translation of a line they can see is untranslated.
    ///
    /// OFF for the document path, where those gates earn their place: that is
    /// the run where 935 lines became 650.
    /// </summary>
    public bool IsStandalone { get; init; }

    /// <summary>
    /// Apply the software-domain vocabulary. ON by default, and that is the
    /// difference from <see cref="UseProjectVocabulary"/>: the project glossary
    /// is a claim about one body of documents and waits for the Memory chip,
    /// while this is what the words of the trade mean in the target language at
    /// all. Measured cost of leaving it off: `Engine`, naming a source project,
    /// came back as `motor`, the machine in a car.
    /// </summary>
    public bool UseDomainVocabulary { get; init; } = true;

    /// <summary>
    /// True when something has to reach the model besides the text itself.
    /// A grounded job cannot use the runtime's own translate call, because that
    /// call builds its prompt from the text alone.
    /// </summary>
    public bool IsGrounded =>
        !string.IsNullOrWhiteSpace(Memory) || !string.IsNullOrWhiteSpace(Instruction);

    /// <summary>
    /// Nothing to do: both sides of the pair are the same language. Decided on
    /// canonical codes by <see cref="Core.Languages.TranslationDirection"/>, so
    /// en and en-GB are the same language here and Chinese in the two scripts is
    /// not.
    /// </summary>
    public bool IsSameLanguage => Direction.IsSameLanguage;

    /// <summary>
    /// Sampling for this job. Temperature is the user's; the token budget is
    /// <see cref="TokenBudget"/>, and it is the only thing Simple and Thinking
    /// change about sampling -- the rest is left at the runtime's own defaults
    /// so a future change there is not silently overridden here.
    /// </summary>
    /// <param name="attempt">
    /// Which try this is, from zero. Only the seed changes with it, and that is
    /// the whole point.
    ///
    /// The runtime seeds deterministically, so the same prompt returns the same
    /// bytes however many times it is sent -- measured across three models and
    /// three temperatures, including 0.7, where two runs were identical every
    /// time. A retry that changes nothing about the request cannot change the
    /// answer, so the second attempt was a full generation spent to be refused
    /// again for exactly the same reason. Moving the seed is what makes it a
    /// second attempt rather than a repeat.
    /// </param>
    public GenParams Sampling(int attempt = 0)
    {
        var p = RuntimeDefaults();

        p.Temperature = Temperature;
        p.MaxTokens = TokenBudget;

        if (Verification.SamplerConfigGuard.AppliesTo(ModelPath))
        {
            var greedy = Verification.SamplerConfigGuard.Recommended();
            p.Temperature = (float)greedy.Temperature;
            p.TopK = greedy.TopK;
            p.TopP = (float)greedy.TopP;
            p.RepeatPenalty = (float)greedy.RepeatPenalty;

            if (attempt > 0 && Engine.Config.PipelineOptions.EscalateRetryDecoding)
            {
                p.Temperature = RetryTemperature;
                p.TopK = RetryTopK;
            }
        }

        if (attempt > 0)
        {
            // Derived from the seed already in force rather than from the clock,
            // so a rerun of the same job takes the same second path and a defect
            // found on attempt two can be reproduced.
            p.Seed = unchecked(p.Seed + (uint)attempt * 2654435761u);
        }

        return p;
    }

    /// <summary>
    /// The runtime's own defaults where there is a runtime to ask, and the
    /// values it was measured to return where there is not.
    ///
    /// Asking has to be optional: the solution must build and test on a machine
    /// with no native build at all, and what sampling a job wants is plain data
    /// that should not need a DLL to work out. Where the library is present it
    /// stays the authority, so a change to its defaults is not overridden here.
    /// </summary>
    private static GenParams RuntimeDefaults()
    {
        try
        {
            return BetterRuntimeModel.DefaultGenParams();
        }
        catch (DllNotFoundException)
        {
            // Measured from br_gen_params_default in BetterRuntime 0.1.0.
            return new GenParams
            {
                MaxTokens = 512,
                Temperature = 0.2f,
                TopP = 0.95f,
                TopK = 40,
                RepeatPenalty = 1f,
                Seed = 0,
            };
        }
    }

    /// <summary>
    /// Enough for a sentence or a short paragraph, which is what the composer
    /// gets used for. The runtime's own default is 512.
    /// </summary>
    /// <summary>
    /// How much the runtime is allowed to generate for this one request.
    ///
    /// Thinking's larger budget is for a document passage, which is the only
    /// thing that can legitimately come back long. A composer unit is already
    /// cut to a line or a sentence before it gets here, so the larger ceiling
    /// buys it nothing and costs it every token a model spends past the answer
    /// before it stops.
    /// </summary>
    private int TokenBudget =>
        Effort == TranslationEffort.Thinking && !IsStandalone ? ThinkingTokens : SimpleTokens;

    private const int SimpleTokens = 512;

    /// <summary>
    /// Room for a long passage. Held well under the 4096-token context so the
    /// prompt, the retrieved block and the output cannot add up past it.
    /// </summary>
    private const int ThinkingTokens = 1536;

    private const float RetryTemperature = 0.15f;

    private const int RetryTopK = 40;
}

/// <summary>
/// Puts retrieved context into the prompt the runtime would have built anyway.
///
/// Measured against BetterRuntime 0.1.0 rather than assumed:
/// br_complete sends its prompt verbatim -- a bare instruction came back as a
/// raw continuation while the same instruction inside chat-template markers was
/// answered properly -- and br_build_translate_prompt followed by br_complete
/// returns byte-for-byte what br_translate returns. So the built prompt is a
/// real, splice-able string and the grounded path is the ungrounded path with a
/// block inserted, not a second prompt format that could drift from it.
/// </summary>
public static class GroundedPrompt
{
    /// <summary>
    /// Puts the block ahead of the translator instruction, inside the user turn.
    ///
    /// Ahead of it, not next to the body: the instruction ends "translate the
    /// following English text into Czech:", so anything placed after it is the
    /// following text. Measured -- with the block sitting there, TranslateGemma
    /// returned a Czech translation of the words "Do not translate it", then the
    /// glossary, and only then the sentence. Before the instruction, the same
    /// block is material and the sentence is still the only thing following.
    ///
    /// The position is derived from the builder rather than written down: two
    /// probe bodies give the invariant prefix, and the block goes just past that
    /// prefix's opening turn marker. Returns null when the built prompt does not
    /// start with the prefix, because every offset here would then be a guess
    /// and a plain translation beats a prompt assembled from one.
    /// </summary>
    public static string? Splice(Func<string, string> build, string body, string? memory, string? instruction)
    {
        var block = Block(memory, instruction);
        var prompt = build(body);

        if (block is null)
        {
            return prompt;
        }

        var prefix = InvariantPrefix(build);

        if (prefix.Length == 0 || !prompt.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return prompt.Insert(AfterOpeningMarker(prefix), block);
    }

    /// <summary>
    /// Everything the builder emits before the body, found by diffing the
    /// prompts for two bodies that share no character. The first position where
    /// they differ is where the body begins.
    /// </summary>
    private static string InvariantPrefix(Func<string, string> build)
    {
        var a = build("aaaa");
        var b = build("bbbb");

        var shared = 0;
        while (shared < a.Length && shared < b.Length && a[shared] == b[shared])
        {
            shared++;
        }

        return a[..shared];
    }

    /// <summary>
    /// Where the instruction starts: past the chat template's opening role
    /// marker, so the block lands inside the turn rather than in front of it.
    /// A prefix that opens with no marker takes the block at the very start.
    /// </summary>
    private static int AfterOpeningMarker(string prefix)
    {
        if (prefix.Length == 0 || prefix[0] != '<')
        {
            return 0;
        }

        var newline = prefix.IndexOf('\n');
        return newline < 0 ? 0 : newline + 1;
    }

    /// <summary>
    /// The block itself. It is headed rather than dumped, so the instruction
    /// that follows reads as being about the sentence and not about this.
    /// </summary>
    public static string? Block(string? memory, string? instruction)
    {
        var hasMemory = !string.IsNullOrWhiteSpace(memory);
        var hasInstruction = !string.IsNullOrWhiteSpace(instruction);

        if (!hasMemory && !hasInstruction)
        {
            return null;
        }

        // Read from configuration rather than written here: this is prompt text,
        // it changes what the model does, and someone tuning output should be
        // able to reach it without a compiler. The constants are the fallback
        // for a file edited into something empty.
        var fragments = BetterTranslator.Engine.Languages.PromptFragments.Current;
        var block = new System.Text.StringBuilder();

        if (hasMemory)
        {
            block.Append(fragments.Text(
                     BetterTranslator.Engine.Languages.PromptFragments.MemoryHeadingId,
                     BetterTranslator.Engine.Languages.PromptFragments.DefaultMemoryHeading))
                 .Append('\n')
                 .Append(memory!.Trim())
                 .Append("\n\n");
        }

        if (hasInstruction)
        {
            block.Append(fragments.Text(
                     BetterTranslator.Engine.Languages.PromptFragments.InstructionLeadInId,
                     BetterTranslator.Engine.Languages.PromptFragments.DefaultInstructionLeadIn))
                 .Append(' ')
                 .Append(instruction!.Trim())
                 .Append("\n\n");
        }

        return block.ToString();
    }
}
