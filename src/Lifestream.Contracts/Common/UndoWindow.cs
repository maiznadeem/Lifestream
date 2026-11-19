namespace Lifestream.Contracts;

/// <summary>
/// How long after a delete the server still accepts a restore (Undo). The API enforces it and the client reads it to decide whether a cancelled or failed
/// Undo can still be retried, so the two can never disagree (SHELL-07, FEAT-06).
/// </summary>
public static class UndoWindow
{
    /// <summary>The client's Undo toast (10 seconds) plus a small grace for the round trip.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(15);
}
