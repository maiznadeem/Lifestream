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
        b.Table("Appointment", ("Status", InList<AppointmentStatus>("Status")));
        b.Property(x => x.PlacedAtUtc).HasDefaultValueSql(UtcNow);
        b.Property(x => x.OverrideReason).HasMaxLength(300);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasOne(x => x.Donor).WithMany().HasForeignKey(x => x.DonorId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.DonorId);
        b.HasIndex(x => new { x.Status, x.SlotStartUtc });
        b.HasIndex(x => x.SlotStartUtc);
        b.HasIndex(x => x.PlacedAtUtc);
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
        b.HasIndex(x => x.PatientId);
        b.HasIndex(x => new { x.Status, x.PlacedAtUtc });
        b.HasIndex(x => new { x.BloodType, x.Status, x.PlacedAtUtc });
        b.HasIndex(x => new { x.Status, x.RequiredBy });
        b.HasIndex(x => x.PlacedAtUtc);
        b.HasIndex(x => x.RequiredBy);
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
        b.HasIndex(x => x.AppointmentId).IsUnique().HasFilter("[AppointmentId] IS NOT NULL");
        b.HasIndex(x => x.RequestId);
        b.HasIndex(x => x.StaffId);
        b.HasIndex(x => new { x.Kind, x.CreatedAtUtc }).IncludeProperties(x => new { x.BloodType, x.Units });
        b.HasIndex(x => x.CreatedAtUtc);
        b.HasIndex(x => new { x.BloodType, x.CreatedAtUtc });
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
