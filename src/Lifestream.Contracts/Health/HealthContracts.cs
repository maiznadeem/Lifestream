namespace Lifestream.Contracts.Health;

/// <param name="Database">One of <see cref="DatabaseStates"/>; "resting" means the free monthly allowance is used up.</param>
public sealed record ReadinessResponse(string Database);

public static class DatabaseStates
{
    public const string Ready = "ready";
    public const string Waking = "waking";
    public const string Resting = "resting";
}
