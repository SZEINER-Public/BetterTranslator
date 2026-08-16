namespace BetterTranslator.Engine.Text;

/// <summary>
/// Conversions that behave the way the PowerShell reference behaves.
///
/// The defect this exists for, found by replaying the reference's own fixture
/// corpus: `[int]$x` in PowerShell ROUNDS -- it goes through
/// `Convert.ToInt32(double)`, which is banker's rounding, half away to even --
/// while `(int)x` in C# TRUNCATES toward zero. Every threshold in the guards is
/// written as `[int](...)`, so a port that casts produces a different number on
/// one side of every boundary.
///
/// It surfaced as a percentage reported as 0% where the reference said 1%. That
/// is cosmetic in a message and is not cosmetic in
/// `if ($outLen -lt [int]($srcLen * $MinLengthRatio))`, where it moves the line
/// between a chunk that is accepted and one that is thrown away.
/// </summary>
public static class PowerShellCast
{
    /// <summary>`[int]` applied to a double, as PowerShell applies it.</summary>
    public static int ToInt(double value) => (int)Math.Round(value, MidpointRounding.ToEven);
}
