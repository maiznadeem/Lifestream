using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class _20261118220716_HardenHashCheckAndListIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff");

        migrationBuilder.DropIndex(
            name: "IX_Request_PatientId",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Patient_BloodType",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Donor_BloodType",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorId",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.AddColumn<string>(
            name: "DonorBloodType",
            table: "Appointment",
            type: "varchar(5)",
            unicode: false,
            maxLength: 5,
            nullable: false,
            defaultValue: "");

        // Existing appointments take their donor's blood type before the CHECK constraint below is added. EXEC defers name
        // resolution, so the idempotent script (one batch with the AddColumn above) compiles.
        migrationBuilder.Sql(
            "EXEC(N'UPDATE a SET a.DonorBloodType = d.BloodType FROM Appointment a JOIN Donor d ON d.Id = a.DonorId')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff",
            sql: "LEN([PasswordHash]) BETWEEN 84 AND 256 AND LEN([PasswordHash]) % 4 = 0 AND [PasswordHash] NOT LIKE '%[^A-Za-z0-9+/=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%=%[^=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%===%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] LIKE 'AQAAAAIA____AAAAE%' COLLATE Latin1_General_100_BIN2");

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request",
            columns: new[] { "BloodType", "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request",
            columns: new[] { "PatientId", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Patient_BloodType_Name",
            table: "Patient",
            columns: new[] { "BloodType", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor",
            columns: new[] { "BloodType", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "SlotStartUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorId_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "DonorId", "SlotStartUtc" })
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment",
            column: "PlacedAtUtc")
            .Annotation("SqlServer:Include", new[] { "SlotStartUtc", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment",
            column: "SlotStartUtc")
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "Status", "PlacedAtUtc" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Appointment_DonorBloodType",
            table: "Appointment",
            sql: "[DonorBloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Patient_BloodType_Name",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorId_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Appointment_DonorBloodType",
            table: "Appointment");

        migrationBuilder.DropColumn(
            name: "DonorBloodType",
            table: "Appointment");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff",
            sql: "LEN([PasswordHash]) BETWEEN 80 AND 256 AND [PasswordHash] LIKE 'AQAAAAI%' COLLATE Latin1_General_100_BIN2");

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId",
            table: "Request",
            column: "PatientId");

        migrationBuilder.CreateIndex(
            name: "IX_Patient_BloodType",
            table: "Patient",
            column: "BloodType");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType",
            table: "Donor",
            column: "BloodType");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorId",
            table: "Appointment",
            column: "DonorId");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment",
            column: "PlacedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment",
            column: "SlotStartUtc");
    }
}
