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

    /// <summary>
    /// The display form for the screen, with a real minus sign (U+2212): "O−". <see cref="ToDisplay"/> stays ASCII ("O-") for search, files and the API.
    /// </summary>
    public static string ToTypographic(this BloodType bloodType) => Display[(int)bloodType].Replace('-', '−');

    /// <summary>
    /// Red-cell compatibility (ABO and RhD): true when red cells of <paramref name="donor"/> may be given to <paramref name="recipient"/>. A donor's A and B
    /// antigens and RhD must all be present in the recipient: O− gives to everyone, AB+ receives from everyone.
    /// </summary>
    public static bool CanGiveTo(this BloodType donor, BloodType recipient)
    {
        var (donorA, donorB, donorD) = Antigens(donor);
        var (recipientA, recipientB, recipientD) = Antigens(recipient);
        return (!donorA || recipientA) && (!donorB || recipientB) && (!donorD || recipientD);
    }

    private static (bool A, bool B, bool D) Antigens(BloodType type) => type switch
    {
        BloodType.APos => (true, false, true),
        BloodType.ANeg => (true, false, false),
        BloodType.BPos => (false, true, true),
        BloodType.BNeg => (false, true, false),
        BloodType.ABPos => (true, true, true),
        BloodType.ABNeg => (true, true, false),
        BloodType.OPos => (false, false, true),
        _ => (false, false, false),
    };

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
