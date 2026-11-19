using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Lifestream.Data.Configurations.ConfigurationHelpers;

namespace Lifestream.Data.Configurations;

internal sealed class AppointmentConfiguration : IEntityTypeConfiguration<Appointment>
{
    public void Configure(EntityTypeBuilder<Appointment> b)
    {
        b.Table("Appointment", ("Status", InList<AppointmentStatus>("Status")), ("DonorBloodType", InList<BloodType>("DonorBloodType")));
        b.Property(x => x.PlacedAtUtc).HasDefaultValueSql(UtcNow);
        b.Property(x => x.OverrideReason).HasMaxLength(300);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Donor).WithMany().HasForeignKey(x => x.DonorId).OnDelete(DeleteBehavior.Restrict);

        // The list screen filters by donor, status, donor blood type and a date range, and sorts by slot or by placed date. Every
        // index below ends in the sort column (after the equality filters) and INCLUDEs the columns the list shows, so a page is read
        // from one narrow index in order and stops after 50 rows: no table scan, no key lookups for the page.
        b.HasIndex(x => x.DonorId); // the narrowest index: counts and joins by donor read this, never the wide ones
        b.HasIndex(x => new { x.DonorId, x.SlotStartUtc }).IncludeProperties(x => new { x.PlacedAtUtc, x.Status });
        b.HasIndex(x => x.SlotStartUtc).IncludeProperties(x => new { x.PlacedAtUtc, x.Status, x.DonorId });
        b.HasIndex(x => x.PlacedAtUtc).IncludeProperties(x => new { x.SlotStartUtc, x.Status, x.DonorId });
        b.HasIndex(x => new { x.Status, x.SlotStartUtc }).IncludeProperties(x => new { x.PlacedAtUtc, x.DonorId });
        b.HasIndex(x => new { x.Status, x.PlacedAtUtc }).IncludeProperties(x => new { x.SlotStartUtc, x.DonorId });
        b.HasIndex(x => new { x.DonorBloodType, x.SlotStartUtc }).IncludeProperties(x => new { x.PlacedAtUtc, x.Status, x.DonorId });
        b.HasIndex(x => new { x.DonorBloodType, x.PlacedAtUtc }).IncludeProperties(x => new { x.SlotStartUtc, x.Status, x.DonorId });
        b.HasIndex(x => new { x.Status, x.DonorBloodType, x.SlotStartUtc }).IncludeProperties(x => new { x.PlacedAtUtc, x.DonorId });
        b.HasIndex(x => new { x.Status, x.DonorBloodType, x.PlacedAtUtc }).IncludeProperties(x => new { x.SlotStartUtc, x.DonorId });
    }
}

internal sealed class RequestConfiguration : IEntityTypeConfiguration<Request>
{
    public void Configure(EntityTypeBuilder<Request> b)
    {
        b.Table(
            "Request",
            ("BloodType", InList<BloodType>("BloodType")),
            ("Status", InList<RequestStatus>("Status")),
            ("Units", "[Units] > 0"));
        b.Property(x => x.PlacedAtUtc).HasDefaultValueSql(UtcNow);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Patient).WithMany().HasForeignKey(x => x.PatientId).OnDelete(DeleteBehavior.Restrict);

        // Same idea as the appointment indexes: filters first, then the sort column, then what the list shows.
        b.HasIndex(x => x.PatientId); // the narrowest index: counts and joins by patient read this, never the wide ones
        b.HasIndex(x => new { x.PatientId, x.PlacedAtUtc }).IncludeProperties(x => new { x.RequiredBy, x.Status, x.BloodType, x.Units });
        b.HasIndex(x => x.PlacedAtUtc).IncludeProperties(x => new { x.PatientId, x.BloodType, x.Units, x.RequiredBy, x.Status });
        b.HasIndex(x => x.RequiredBy).IncludeProperties(x => new { x.PatientId, x.BloodType, x.Units, x.PlacedAtUtc, x.Status });
        b.HasIndex(x => new { x.Status, x.PlacedAtUtc }).IncludeProperties(x => new { x.PatientId, x.BloodType, x.Units, x.RequiredBy });
        b.HasIndex(x => new { x.Status, x.RequiredBy }).IncludeProperties(x => new { x.PatientId, x.BloodType, x.Units, x.PlacedAtUtc });
        b.HasIndex(x => new { x.BloodType, x.PlacedAtUtc }).IncludeProperties(x => new { x.PatientId, x.Units, x.RequiredBy, x.Status });
        b.HasIndex(x => new { x.BloodType, x.RequiredBy }).IncludeProperties(x => new { x.PatientId, x.Units, x.PlacedAtUtc, x.Status });
        b.HasIndex(x => new { x.BloodType, x.Status, x.PlacedAtUtc }).IncludeProperties(x => new { x.PatientId, x.Units, x.RequiredBy });
        b.HasIndex(x => new { x.BloodType, x.Status, x.RequiredBy }).IncludeProperties(x => new { x.PatientId, x.Units, x.PlacedAtUtc });
    }
}

internal sealed class ReceiptConfiguration : IEntityTypeConfiguration<Receipt>
{
    public void Configure(EntityTypeBuilder<Receipt> b)
    {
        b.Table(
            "Receipt",
            ("Kind", InList<ReceiptKind>("Kind")),
            ("BloodType", InList<BloodType>("BloodType")),
            ("Units", "[Units] > 0"),
            (
                "Links",
                "([Kind] = 'Donation' AND [AppointmentId] IS NOT NULL AND [RequestId] IS NULL) " +
                "OR ([Kind] = 'Issue' AND [RequestId] IS NOT NULL AND [AppointmentId] IS NULL)"));
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql(UtcNow);
        b.Property(x => x.Notes).HasMaxLength(500);
        b.HasOne(x => x.Staff).WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Appointment).WithMany().HasForeignKey(x => x.AppointmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.Request).WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);

        // An appointment can be receipted once. The filtered unique index also supports the foreign key.
        // The three foreign keys also carry date, kind and blood type: the receipt search finds, filters and orders its matches from them.
        b.HasIndex(x => x.AppointmentId).IsUnique().HasFilter("[AppointmentId] IS NOT NULL").IncludeProperties(x => new { x.CreatedAtUtc, x.Kind, x.BloodType });
        b.HasIndex(x => x.RequestId).HasFilter("[RequestId] IS NOT NULL").IncludeProperties(x => new { x.CreatedAtUtc, x.Kind, x.BloodType });
        b.HasIndex(x => x.StaffId); // the narrowest index of the table: COUNT(*) reads this
        b.HasIndex(x => new { x.StaffId, x.CreatedAtUtc }).IncludeProperties(x => new { x.Kind, x.BloodType });

        // Receipt list (and the dashboard series, which reads kind, blood type, units and date): narrow, ordered, covering.
        b.HasIndex(x => new { x.Kind, x.CreatedAtUtc }).IncludeProperties(x => new { x.BloodType, x.Units, x.StaffId, x.AppointmentId, x.RequestId });
        b.HasIndex(x => x.CreatedAtUtc).IncludeProperties(x => new { x.Kind, x.BloodType, x.Units, x.StaffId, x.AppointmentId, x.RequestId });
        b.HasIndex(x => new { x.BloodType, x.CreatedAtUtc }).IncludeProperties(x => new { x.Kind, x.Units, x.StaffId, x.AppointmentId, x.RequestId });
        b.HasIndex(x => new { x.Kind, x.BloodType, x.CreatedAtUtc }).IncludeProperties(x => new { x.Units, x.StaffId, x.AppointmentId, x.RequestId });
    }
}

internal sealed class BloodBagConfiguration : IEntityTypeConfiguration<BloodBag>
{
    public void Configure(EntityTypeBuilder<BloodBag> b)
    {
        b.Table(
            "BloodBag",
            ("BloodType", InList<BloodType>("BloodType")),
            ("Status", InList<BagStatus>("Status")),
            ("IssueLink", "([Status] = 'Issued' AND [IssueReceiptId] IS NOT NULL) OR ([Status] <> 'Issued' AND [IssueReceiptId] IS NULL)"));
        b.HasOne(x => x.DonationReceipt).WithMany().HasForeignKey(x => x.DonationReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(x => x.IssueReceipt).WithMany().HasForeignKey(x => x.IssueReceiptId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.DonationReceiptId);
        b.HasIndex(x => x.IssueReceiptId);
        b.HasIndex(x => new { x.BloodType, x.Status, x.CollectedAtUtc });
        b.HasIndex(x => x.CollectedAtUtc);

        // Stock by blood type reads only this narrow index: available bags in expiry order.
        b.HasIndex(x => new { x.ExpiresOn, x.BloodType }).HasFilter("[Status] = 'Available'");

        // The oldest-first pick of a fulfil (BloodBagSql.IssueOldestFirst): one seek on the blood type and the expiry date (only unexpired bags of the type
        // are read, at most the stock of that type), with the collection date and the id already in the index, so the pick sorts those few rows and the
        // clustered index is touched only to update the bags chosen.
        b.HasIndex(x => new { x.BloodType, x.ExpiresOn }).IncludeProperties(x => x.CollectedAtUtc).HasFilter("[Status] = 'Available'");
    }
}

internal sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> b)
    {
        b.Table("AuditLog", ("Action", $"[Action] IN ({string.Join(", ", AuditActions.All.Select(a => $"'{a}'"))})"));
        b.Property(x => x.Action).HasMaxLength(10).IsUnicode(false);
        b.Property(x => x.Entity).HasMaxLength(30).IsUnicode(false);
        b.Property(x => x.OccurredAtUtc).HasDefaultValueSql(UtcNow);
        b.HasIndex(x => new { x.Entity, x.EntityId });
        b.HasIndex(x => x.OccurredAtUtc);
        b.HasIndex(x => x.StaffId);
    }
}

internal sealed class DemoStateConfiguration : IEntityTypeConfiguration<DemoState>
{
    public void Configure(EntityTypeBuilder<DemoState> b)
    {
        b.Table("DemoState", ("SingleRow", "[Id] = 1"));
        b.Property(x => x.Id).ValueGeneratedNever();
    }
}
