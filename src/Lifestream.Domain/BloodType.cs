namespace Lifestream.Domain;

/// <summary>The eight ABO/Rh blood types. Stored and sent as the enum name; shown as "A+" etc.</summary>
public enum BloodType
{
    APos,
    ANeg,
    BPos,
    BNeg,
    ABPos,
    ABNeg,
    OPos,
    ONeg,
}

public static class BloodTypeExtensions
{
    private static readonly string[] Display = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

    /// <summary>All blood types in display order.</summary>
    public static IReadOnlyList<BloodType> All { get; } = Enum.GetValues<BloodType>();

    public static string ToDisplay(this BloodType bloodType) => Display[(int)bloodType];

    /// <summary>Parses "A+", "ab-" and similar (case-insensitive, trimmed). Returns false for anything else.</summary>
    public static bool TryParseDisplay(string? text, out BloodType bloodType)
    {
        bloodType = default;
        if (text is null)
        {
            return false;
        }

        var trimmed = text.Trim();
        for (var i = 0; i < Display.Length; i++)
        {
            if (string.Equals(Display[i], trimmed, StringComparison.OrdinalIgnoreCase))
            {
                bloodType = (BloodType)i;
                return true;
            }
        }

        return false;
    }
}
