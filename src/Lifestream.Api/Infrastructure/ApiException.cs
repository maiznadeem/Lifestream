using Lifestream.Contracts;

namespace Lifestream.Api.Infrastructure;

/// <summary>
/// An expected failure that becomes an RFC 7807 response. Thrown by endpoint code, caught once by <see cref="ProblemMiddleware"/>.
/// The message is written to the client, so it must never carry exception, SQL or schema text.
/// </summary>
public sealed class ApiException(int status, string code, string title, IReadOnlyDictionary<string, string[]>? errors = null, TimeSpan? retryAfter = null)
    : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string Title { get; } = title;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public TimeSpan? RetryAfter { get; } = retryAfter;

    public static ApiException NotFound(string what) => new(StatusCodes.Status404NotFound, ProblemCodes.NotFound, $"{what} was not found.");

    public static ApiException Conflict(string title, string code = ProblemCodes.Conflict) => new(StatusCodes.Status409Conflict, code, title);

    public static ApiException Validation(string field, string message) =>
        new(StatusCodes.Status400BadRequest, ProblemCodes.Validation, "One or more values are not valid.", new Dictionary<string, string[]> { [field] = [message] });

    public static ApiException Validation(IReadOnlyDictionary<string, string[]> errors) =>
        new(StatusCodes.Status400BadRequest, ProblemCodes.Validation, "One or more values are not valid.", errors);

    public static ApiException Forbidden(string title = "You do not have permission to do this.") => new(StatusCodes.Status403Forbidden, ExtraProblemCodes.Forbidden, title);

    public static ApiException DemoForbidden() =>
        new(StatusCodes.Status403Forbidden, ProblemCodes.DemoForbidden, "The demo account cannot do this.");
}
