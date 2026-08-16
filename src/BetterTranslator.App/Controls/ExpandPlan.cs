namespace BetterTranslator.App.Controls;

public readonly record struct ExpandPlan(bool Animates, double From, double To)
{
    public static ExpandPlan For(double current, double target, bool motionEnabled)
    {
        if (!motionEnabled)
        {
            return new ExpandPlan(false, target, target);
        }

        return new ExpandPlan(!AlreadyThere(current, target), current, target);
    }

    private static bool AlreadyThere(double current, double target) =>
        Math.Abs(current - target) < 0.5;
}
