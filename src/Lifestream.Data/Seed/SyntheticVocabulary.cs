using System.Globalization;
using System.Text;

namespace Lifestream.Data.Seed;

/// <summary>
/// The only source of names, phone numbers and addresses in seed data. Everything is invented: names are pairs from
/// two lists, phones sit in the fictional range +1-555-0100 to +1-555-0199, addresses are "Sample" streets in "Demoville".
/// </summary>
public static class SyntheticVocabulary
{
    public static IReadOnlyList<string> FirstNames { get; } =
    [
        "Aaliyah", "Adrian", "Aiko", "Alma", "Amara", "Anders", "Anika", "Aria", "Asher", "Ayşe",
        "Beatrix", "Bence", "Bianca", "Callum", "Camila", "Caspian", "Celeste", "Chiara", "Cyrus", "Dalia",
        "Dario", "Delphine", "Dmitri", "Elara", "Elio", "Emre", "Esme", "Farid", "Felix", "Fenna",
        "Freya", "Gabriel", "Greta", "Hana", "Idris", "Ilse", "Imani", "Ingrid", "Jasper", "Joaquín",
        "Junia", "Kaito", "Kalani", "Katya", "Leif", "Lucia", "Mateo", "Mei", "Mirela", "Nadia",
        "Naveen", "Niamh", "Noor", "Odalys", "Oskar", "Paloma", "Petra", "Quinn", "Rafael", "Renata",
        "Rosalind", "Saoirse", "Selim", "Soren", "Sunita", "Talia", "Tomás", "Ulrich", "Valentina", "Vikram",
        "Wren", "Xavier", "Yara", "Yusuf", "Zainab", "Zephyr", "Ottoline", "Lior",
    ];

    public static IReadOnlyList<string> LastNames { get; } =
    [
        "Abernathy", "Alvarado", "Ashworth", "Bellamy", "Birch", "Blackwood", "Calloway", "Castellanos", "Chandrasekar", "Delacroix",
        "Dunmore", "Eastwood", "Ellsworth", "Fairbanks", "Fontaine", "Gallagher", "Garrido", "Hargreaves", "Holloway", "Ishikawa",
        "Jankowski", "Kowalczyk", "Lindqvist", "Lockhart", "Marchetti", "Merriweather", "Montague", "Nakamura", "Northcott", "Novak",
        "Oyelaran", "Öztürk", "Pemberton", "Quintero", "Radcliffe", "Rosenthal", "Salazar", "Sandoval", "Thornbury", "Tremblay",
        "Underhill", "Valdivia", "Vandermeer", "Wainwright", "Whitlock", "Yamamoto", "Zielinski", "Abdulrahman", "Achterberg", "Bergström",
        "Cavanaugh", "Dragomir", "Echevarría", "Fitzgerald", "Grimaldi", "Haddad", "Iversen", "Jovanović", "Kingsley", "Lavoie",
        "Mukherjee", "Ndiaye", "Olsen", "Pellegrino", "Rutherford", "Svensson", "Tanaka", "Ulloa", "Villanueva", "Wójcik",
        "Yildiz", "Zamora", "Ashby", "Brightwater", "Crowhurst", "Devereux", "Farrow", "Greenleaf",
    ];

    public const string DemoStaffName = "Demo Staff";

    /// <summary>Phone in the reserved fictional range +1-555-0100 .. +1-555-0199.</summary>
    public static string Phone(int number) => $"+1-555-01{number % 100:D2}";

    public static string Address(int houseNumber, int street) =>
        $"{houseNumber} Sample {StreetNames[street % StreetNames.Length]}, Demoville";

    private static readonly string[] StreetNames = ["Street", "Road", "Avenue", "Lane", "Court", "Terrace"];

    /// <summary>
    /// The i-th person's name. Names are unique for every index: a mixed-radix split into first name, last name and
    /// (beyond the first full grid) a middle initial. <paramref name="index"/> should already be shuffled by the caller.
    /// </summary>
    public static string PersonName(int index)
    {
        var first = FirstNames[index % FirstNames.Count];
        var rest = index / FirstNames.Count;
        var last = LastNames[rest % LastNames.Count];
        var grid = rest / LastNames.Count;
        return grid == 0 ? $"{first} {last}" : $"{first} {(char)('A' + ((grid - 1) % 26))}. {last}";
    }

    /// <summary>Lower-case ASCII user name derived from a person name, e.g. "Ayşe Öztürk" gives "ayse.ozturk".</summary>
    public static string UsernameFor(string personName)
    {
        var decomposed = personName.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (c == ' ')
            {
                sb.Append('.');
            }
            else if (char.IsAsciiLetter(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString().Replace("..", ".", StringComparison.Ordinal);
    }

    /// <summary>True when the text is a name this vocabulary can produce (used by tests).</summary>
    public static bool IsSyntheticName(string name)
    {
        var parts = name.Split(' ');
        return parts.Length switch
        {
            2 => FirstNames.Contains(parts[0]) && LastNames.Contains(parts[1]),
            3 => FirstNames.Contains(parts[0]) && parts[1].Length == 2 && char.IsAsciiLetterUpper(parts[1][0]) && parts[1][1] == '.' && LastNames.Contains(parts[2]),
            _ => false,
        };
    }
}
