using Lifestream.Data.Entities;
using Lifestream.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using static Lifestream.Data.Configurations.ConfigurationHelpers;

namespace Lifestream.Data.Configurations;

internal sealed class DonorConfiguration : IEntityTypeConfiguration<Donor>
{
    public void Configure(EntityTypeBuilder<Donor> b)
    {
        b.Table(
            "Donor",
            ("BloodType", InList<BloodType>("BloodType")),
            ("Gender", InList<Gender>("Gender")),
            ("Frequency", InList<DonorFrequency>("Frequency")));
        b.Property(x => x.Name).HasMaxLength(100).UseCollation(NameCollation);
        b.Property(x => x.Phone).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Address).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Name);
        b.HasIndex(x => x.BloodType);
        b.HasIndex(x => x.Gender);
        b.HasIndex(x => x.LastDonated);
    }
}

internal sealed class PatientConfiguration : IEntityTypeConfiguration<Patient>
{
    public void Configure(EntityTypeBuilder<Patient> b)
    {
        b.Table(
            "Patient",
            ("BloodType", InList<BloodType>("BloodType")),
            ("Gender", InList<Gender>("Gender")));
        b.Property(x => x.Name).HasMaxLength(100).UseCollation(NameCollation);
        b.Property(x => x.Phone).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.Address).HasMaxLength(200);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Name);
        b.HasIndex(x => x.BloodType);
        b.HasIndex(x => x.Gender);
    }
}

internal sealed class StaffConfiguration : IEntityTypeConfiguration<Staff>
{
    /// <summary>
    /// ASP.NET Core Identity v3 hashes are base64 and start with the bytes 01 00 00 00 02 (format marker, then PRF
    /// HMAC-SHA512), which is always "AQAAAAI". Compared with a binary collation so case matters. Plain text cannot pass.
    /// </summary>
    public const string HashCheck =
        "LEN([PasswordHash]) BETWEEN 80 AND 256 AND [PasswordHash] LIKE 'AQAAAAI%' COLLATE Latin1_General_100_BIN2";

    public void Configure(EntityTypeBuilder<Staff> b)
    {
        b.Table(
            "Staff",
            ("Gender", InList<Gender>("Gender")),
            ("Designation", InList<Designation>("Designation")),
            ("Shift", InList<Shift>("Shift")),
            ("Role", InList<Role>("Role")),
            ("PasswordHash", HashCheck),
            ("Salary", "[Salary] IS NULL OR [Salary] >= 0"));
        b.Property(x => x.DisplayName).HasMaxLength(100).UseCollation(NameCollation);
        b.Property(x => x.Username).HasMaxLength(50).UseCollation(UsernameCollation);
        b.Property(x => x.PasswordHash).HasMaxLength(256).IsUnicode(false);
        b.Property(x => x.Phone).HasMaxLength(20).IsUnicode(false);
        b.Property(x => x.RowVersion).IsRowVersion();
        b.HasIndex(x => x.Username).IsUnique();
        b.HasIndex(x => x.DisplayName);
    }
}

internal sealed class StaffPhotoConfiguration : IEntityTypeConfiguration<StaffPhoto>
{
    public void Configure(EntityTypeBuilder<StaffPhoto> b)
    {
        b.Table(
            "StaffPhoto",
            ("Size", $"DATALENGTH([Content]) BETWEEN 1 AND {StaffPhoto.MaxBytes}"),
            ("Pixels", $"[Width] BETWEEN 1 AND {StaffPhoto.MaxPixels} AND [Height] BETWEEN 1 AND {StaffPhoto.MaxPixels}"));
        b.HasKey(x => x.StaffId);
        b.Property(x => x.Content).HasMaxLength(StaffPhoto.MaxBytes);
        b.Property(x => x.ContentType).HasMaxLength(50).IsUnicode(false);
        b.Property(x => x.UpdatedAtUtc).HasDefaultValueSql(UtcNow);
        b.HasOne(x => x.Staff).WithOne().HasForeignKey<StaffPhoto>(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> b)
    {
        b.Table("RefreshToken");
        b.Property(x => x.TokenHash).HasMaxLength(128).IsUnicode(false);
        b.Property(x => x.CreatedAtUtc).HasDefaultValueSql(UtcNow);
        b.HasOne(x => x.Staff).WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(x => x.TokenHash).IsUnique();
        b.HasIndex(x => x.StaffId);
        b.HasIndex(x => x.FamilyId);
        b.HasIndex(x => x.ExpiresAtUtc);
    }
}
