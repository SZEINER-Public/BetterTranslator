using System.Globalization;

namespace BetterTranslator.Updates.Releases;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string PreRelease)
    : IComparable<SemanticVersion>
{
    private const int MaxTextLength = 64;
    private const int MaxDigits = 9;

    public static SemanticVersion Parse(string text) =>
        TryParse(text, out var version)
            ? version
            : throw new FormatException($"\"{text}\" is not a version this reads.");

    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = new SemanticVersion(0, 0, 0, string.Empty);

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return false;
        }

        var body = text.Trim();

        if (body[0] is 'v' or 'V')
        {
            body = body[1..];
        }

        var metadata = body.IndexOf('+');

        if (metadata >= 0)
        {
            body = body[..metadata];
        }

        var preRelease = string.Empty;
        var dash = body.IndexOf('-');

        if (dash >= 0)
        {
            preRelease = body[(dash + 1)..];
            body = body[..dash];

            if (!IsPreRelease(preRelease))
            {
                return false;
            }
        }

        if (body.Length == 0)
        {
            return false;
        }

        var parts = body.Split('.');

        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        var numbers = new int[3];

        for (var index = 0; index < parts.Length; index++)
        {
            if (!TryNumber(parts[index], out numbers[index]))
            {
                return false;
            }
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2], preRelease);

        return true;
    }

    public int CompareTo(SemanticVersion other)
    {
        var order = Major.CompareTo(other.Major);

        if (order != 0)
        {
            return order;
        }

        order = Minor.CompareTo(other.Minor);

        if (order != 0)
        {
            return order;
        }

        order = Patch.CompareTo(other.Patch);

        return order != 0 ? order : ComparePreRelease(PreRelease ?? string.Empty, other.PreRelease ?? string.Empty);
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    public override string ToString() =>
        PreRelease is { Length: > 0 }
            ? string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}-{PreRelease}")
            : string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");

    private static bool TryNumber(string part, out int value)
    {
        value = 0;

        if (part.Length is 0 or > MaxDigits)
        {
            return false;
        }

        if (part.Length > 1 && part[0] == '0')
        {
            return false;
        }

        foreach (var character in part)
        {
            if (!char.IsAsciiDigit(character))
            {
                return false;
            }
        }

        return int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsPreRelease(string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        foreach (var identifier in text.Split('.'))
        {
            if (!IsIdentifier(identifier))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsIdentifier(string identifier)
    {
        if (identifier.Length == 0)
        {
            return false;
        }

        var numeric = true;

        foreach (var character in identifier)
        {
            if (char.IsAsciiDigit(character))
            {
                continue;
            }

            if (!char.IsAsciiLetter(character) && character != '-')
            {
                return false;
            }

            numeric = false;
        }

        return !numeric || identifier.Length == 1 || identifier[0] != '0';
    }

    private static int ComparePreRelease(string left, string right)
    {
        if (left.Length == 0 && right.Length == 0)
        {
            return 0;
        }

        if (left.Length == 0)
        {
            return 1;
        }

        if (right.Length == 0)
        {
            return -1;
        }

        var mine = left.Split('.');
        var theirs = right.Split('.');
        var shared = Math.Min(mine.Length, theirs.Length);

        for (var index = 0; index < shared; index++)
        {
            var order = CompareIdentifier(mine[index], theirs[index]);

            if (order != 0)
            {
                return order;
            }
        }

        return mine.Length.CompareTo(theirs.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftNumeric = TryNumber(left, out var leftValue);
        var rightNumeric = TryNumber(right, out var rightValue);

        if (leftNumeric && rightNumeric)
        {
            return leftValue.CompareTo(rightValue);
        }

        if (leftNumeric)
        {
            return -1;
        }

        if (rightNumeric)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
