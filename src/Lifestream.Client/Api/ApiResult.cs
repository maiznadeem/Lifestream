namespace Lifestream.Client.Api;

/// <summary>A failed call, in the shape of the API's RFC 7807 response (or synthesised by the client when no response arrived).</summary>
public sealed record ApiProblem(int Status, string Code, string Title, string? CorrelationId = null, string? Detail = null, IReadOnlyDictionary<string, string[]>? Errors = null)
{
    /// <summary>The messages for one field (the problem's <c>errors</c> object), or none.</summary>
    public IReadOnlyList<string> ErrorsFor(string field) =>
        Errors is not null && Errors.TryGetValue(field, out var messages) ? messages : [];

    /// <summary>True when the server or database is starting up (or the call gave up waiting for it).</summary>
    public bool IsWaking => Code == Contracts.ProblemCodes.DatabaseWaking;

    /// <summary>True when the demo database used up its free allowance and is paused until next month.</summary>
    public bool IsResting => Code == Contracts.ProblemCodes.DatabaseResting;

    /// <summary>True when no response was received at all.</summary>
    public bool IsUnreachable => Code == ClientProblemCodes.Unreachable;
}

/// <summary>Problem codes the client creates itself when there is no server response.</summary>
public static class ClientProblemCodes
{
    public const string Unreachable = "client_unreachable";
    public const string BadResponse = "client_bad_response";
    public const string SessionExpired = "client_session_expired";
}

/// <summary>Outcome of an API call without a body: either ok or a <see cref="ApiProblem"/>. Never an exception.</summary>
public class ApiResult
{
    private protected ApiResult(ApiProblem? problem) => Problem = problem;

    public ApiProblem? Problem { get; }

    public bool IsOk => Problem is null;

    public static ApiResult Success { get; } = new(null);

    public static ApiResult Fail(ApiProblem problem) => new(problem);

    /// <summary>Throws <see cref="ApiProblemException"/> when the call failed.</summary>
    public void ThrowIfFailed()
    {
        if (Problem is not null)
        {
            throw new ApiProblemException(Problem);
        }
    }
}

/// <summary>Outcome of an API call with a body.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Ok and Fail are the factory methods of this result type.")]
public sealed class ApiResult<T> : ApiResult
{
    private readonly T? _value;

    private ApiResult(T? value, ApiProblem? problem)
        : base(problem) => _value = value;

    public T Value => _value ?? throw new InvalidOperationException("The call failed; there is no value.");

    public static ApiResult<T> Ok(T value) => new(value, null);

    public static new ApiResult<T> Fail(ApiProblem problem) => new(default, problem);

    /// <summary>Returns the value or throws <see cref="ApiProblemException"/>; view models call this inside their load.</summary>
    public T Unwrap()
    {
        ThrowIfFailed();
        return Value;
    }
}

/// <summary>Carries an <see cref="ApiProblem"/> out of a view model's load so the base class can show the right state.</summary>
public sealed class ApiProblemException(ApiProblem problem) : Exception(problem.Title)
{
    public ApiProblem Problem { get; } = problem;
}
