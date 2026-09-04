using System.Globalization;
using BetterTranslator.Core.Verification.Checks;
using BetterTranslator.Core.Verification.Checks.Semantics;

namespace BetterTranslator.Core.Verification.Gate;

public sealed record EscalationSummary(
    int Flagged,
    int Admitted,
    int Refused,
    int Cap,
    int ReverseCap,
    int ReverseCalls,
    int ReverseTokens,
    int ReverseDurationMs)
{
    public string Text
    {
        get
        {
            var culture = CultureInfo.InvariantCulture;
            var head = string.Create(culture, $"{Admitted} of {Flagged} flagged spans re-checked, cap {Cap}");
            var tail = ReverseCap > 0
                ? string.Create(culture, $", {ReverseCalls} of at most {ReverseCap} reverse translation call(s), {ReverseTokens} token(s)")
                : ", reverse translation off";

            return head + tail;
        }
    }

    public static EscalationSummary? For(CheckContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (EscalationGate.DecisionFor(context) is not { } decision)
        {
            return null;
        }

        var services = SemanticPorts.For(context);
        var reverse = services.Reverse;

        return new EscalationSummary(
            decision.Flagged,
            decision.Admitted.Count,
            decision.Refused,
            decision.Cap,
            services.Settings.ReverseCap,
            reverse.Calls,
            reverse.GeneratedTokens,
            (int)reverse.Elapsed.TotalMilliseconds);
    }
}
