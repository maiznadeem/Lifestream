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
        b.HasIndex(x => new { x.BloodType, x.Name });
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
        b.HasIndex(x => new { x.BloodType, x.Name });
        b.HasIndex(x => x.Gender);
    }
}

internal sealed class StaffConfiguration : IEntityTypeConfiguration<Staff>
{
    /// <summary>
    /// The most of an ASP.NET Core Identity v3 hash that a T-SQL CHECK can verify (no function may be created, and XML methods
    /// are not allowed in a CHECK, so the base64 text is inspected with LIKE under a binary collation, where case matters):
    /// <list type="bullet">
    /// <item>base64 alphabet only (<c>A-Za-z0-9+/</c>), padding <c>=</c> only at the end and at most two;</item>
    /// <item>a length that is a multiple of 4, at least 84 characters (13 header bytes + 16 salt bytes + 32 subkey bytes, 61
    /// bytes decoded) and at most 256;</item>
    /// <item>the exact header: byte 0 = 0x01 (v3), bytes 1-4 = 2 (PRF HMAC-SHA512), byte 5 = 0 (so under 2^24 iterations),
    /// bytes 9-12 = 16 (salt length). Base64 characters 1-8 are therefore <c>AQAAAAIA</c> and 13-17 are <c>AAAAE</c>.</item>
    /// </list>
    /// What it cannot check: the iteration count floor (the application's <c>PasswordHashing.IsValidHash</c> does) and that
    /// the salt and subkey are random. Plain text, and plain text behind a valid-looking prefix, cannot pass.
    /// </summary>
    public const string HashCheck =
        "LEN([PasswordHash]) BETWEEN 84 AND 256 AND LEN([PasswordHash]) % 4 = 0 " +
        "AND [PasswordHash] NOT LIKE '%[^A-Za-z0-9+/=]%' COLLATE Latin1_General_100_BIN2 " +
        "AND [PasswordHash] NOT LIKE '%=%[^=]%' COLLATE Latin1_General_100_BIN2 " +
        "AND [PasswordHash] NOT LIKE '%===%' COLLATE Latin1_General_100_BIN2 " +
        "AND [PasswordHash] LIKE 'AQAAAAIA____AAAAE%' COLLATE Latin1_General_100_BIN2";

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
