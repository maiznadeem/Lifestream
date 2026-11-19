using System.Text.RegularExpressions;

namespace Lifestream.Client.Validation;

/// <summary>
/// The one phone rule of every form, the same as the one the API enforces (<c>Check.Phone</c>): an optional leading +, then 5 to 20 characters that are
/// digits, spaces, brackets or dashes. The text and the rule are written together, so the message can never promise what the rule refuses
/// (for example "(555)0101" is accepted because brackets are allowed anywhere after the +).
/// </summary>
public static partial class PhoneRule
{
    /// <summary>The most characters a phone box accepts: a + and 20 more.</summary>
    public const int MaxTypedLength = 21;

    /// <summary>The message under a phone field that fails the rule; it states the rule exactly.</summary>
    public const string Message = "Use 5 to 20 characters: digits, spaces, dashes and brackets, with an optional + at the start.";

    /// <summary>The pattern, character for character the API's.</summary>
    public const string Pattern = @"^\+?[0-9 ()\-]{5,20}$";

    /// <summary>Null when <paramref name="phone"/> (already trimmed, not empty) follows the rule; otherwise <see cref="Message"/>.</summary>
    public static string? Validate(string phone) => Matcher().IsMatch(phone) ? null : Message;

    [GeneratedRegex(Pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Matcher();
}
