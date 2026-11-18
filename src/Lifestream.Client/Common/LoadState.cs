namespace Lifestream.Client.Common;

/// <summary>What a data surface is showing. Loading and Waking show skeletons; Error offers Retry.</summary>
public enum LoadState
{
    Loading,
    Ready,
    Empty,
    Error,
    Waking,
}
