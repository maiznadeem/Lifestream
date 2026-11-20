using Lifestream.Client.Features.Requests;

namespace Lifestream.Client.Features.Patients;

/// <summary>One of a patient's waiting requests in their side panel: what is needed, by when, how urgent, and whether today's stock covers it (null when unknown).</summary>
public sealed record PatientRequestRow(int Id, string Title, string NeededText, RequestUrgency Urgency, RequestCoverage? Cover)
{
    /// <summary>"R-0148".</summary>
    public string Code => RequestsViewModel.Code(Id);

    /// <summary>Why the card offers no Fulfil (none of the group in stock), said next to the disabled button; null when it can be fulfilled.</summary>
    public string? HoldReason { get; init; }

    /// <summary>False while there is none of the group in stock: Fulfil would fail, so it is disabled and <see cref="HoldReason"/> says why.</summary>
    public bool CanFulfil => HoldReason is null;
}
