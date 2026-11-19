using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class _20261118212125_InitialSchema : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AuditLog",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                StaffId = table.Column<int>(type: "int", nullable: true),
                Action = table.Column<string>(type: "varchar(10)", unicode: false, maxLength: 10, nullable: false),
                Entity = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                EntityId = table.Column<int>(type: "int", nullable: false),
                OccurredAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditLog", x => x.Id);
                table.CheckConstraint("CK_AuditLog_Action", "[Action] IN ('Insert', 'Update', 'Delete')");
            });

        migrationBuilder.CreateTable(
            name: "DemoState",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false),
                LastResetDate = table.Column<DateOnly>(type: "date", nullable: true),
                LastForcedResetAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DemoState", x => x.Id);
                table.CheckConstraint("CK_DemoState_SingleRow", "[Id] = 1");
            });

        migrationBuilder.CreateTable(
            name: "Donor",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AI"),
                DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                Gender = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                BloodType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                Frequency = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                LastDonated = table.Column<DateOnly>(type: "date", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Donor", x => x.Id);
                table.CheckConstraint("CK_Donor_BloodType", "[BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
                table.CheckConstraint("CK_Donor_Frequency", "[Frequency] IN ('Regular', 'Occasional')");
                table.CheckConstraint("CK_Donor_Gender", "[Gender] IN ('Male', 'Female', 'Other')");
            });

        migrationBuilder.CreateTable(
            name: "Patient",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AI"),
                DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                Gender = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                BloodType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                Address = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Patient", x => x.Id);
                table.CheckConstraint("CK_Patient_BloodType", "[BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
                table.CheckConstraint("CK_Patient_Gender", "[Gender] IN ('Male', 'Female', 'Other')");
            });

        migrationBuilder.CreateTable(
            name: "Staff",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                DisplayName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false, collation: "Latin1_General_100_CI_AI"),
                Username = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false, collation: "Latin1_General_100_CI_AS"),
                PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                Gender = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                DateOfBirth = table.Column<DateOnly>(type: "date", nullable: false),
                Designation = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                Shift = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                Role = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                Salary = table.Column<int>(type: "int", nullable: true),
                Phone = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                IsProtected = table.Column<bool>(type: "bit", nullable: false),
                IsDemo = table.Column<bool>(type: "bit", nullable: false),
                HasPhoto = table.Column<bool>(type: "bit", nullable: false),
                FailedLoginCount = table.Column<int>(type: "int", nullable: false),
                LockoutEndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Staff", x => x.Id);
                table.CheckConstraint("CK_Staff_Designation", "[Designation] IN ('Admin', 'Doctor', 'Worker', 'Assistant')");
                table.CheckConstraint("CK_Staff_Gender", "[Gender] IN ('Male', 'Female', 'Other')");
                table.CheckConstraint("CK_Staff_PasswordHash", "LEN([PasswordHash]) BETWEEN 80 AND 256 AND [PasswordHash] LIKE 'AQAAAAI%' COLLATE Latin1_General_100_BIN2");
                table.CheckConstraint("CK_Staff_Role", "[Role] IN ('Admin', 'Staff')");
                table.CheckConstraint("CK_Staff_Salary", "[Salary] IS NULL OR [Salary] >= 0");
                table.CheckConstraint("CK_Staff_Shift", "[Shift] IN ('Morning', 'Evening', 'FullTime')");
            });

        migrationBuilder.CreateTable(
            name: "Appointment",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                DonorId = table.Column<int>(type: "int", nullable: false),
                PlacedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                SlotStartUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                OverrideReason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Appointment", x => x.Id);
                table.CheckConstraint("CK_Appointment_Status", "[Status] IN ('Pending', 'Completed', 'Cancelled', 'NoShow')");
                table.ForeignKey(
                    name: "FK_Appointment_Donor_DonorId",
                    column: x => x.DonorId,
                    principalTable: "Donor",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Request",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                PatientId = table.Column<int>(type: "int", nullable: false),
                BloodType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                Units = table.Column<int>(type: "int", nullable: false),
                PlacedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                RequiredBy = table.Column<DateOnly>(type: "date", nullable: false),
                Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Request", x => x.Id);
                table.CheckConstraint("CK_Request_BloodType", "[BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
                table.CheckConstraint("CK_Request_Status", "[Status] IN ('Pending', 'Fulfilled', 'Cancelled')");
                table.CheckConstraint("CK_Request_Units", "[Units] > 0");
                table.ForeignKey(
                    name: "FK_Request_Patient_PatientId",
                    column: x => x.PatientId,
                    principalTable: "Patient",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "RefreshToken",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                StaffId = table.Column<int>(type: "int", nullable: false),
                FamilyId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                TokenHash = table.Column<string>(type: "varchar(128)", unicode: false, maxLength: 128, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                RevokedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RefreshToken", x => x.Id);
                table.ForeignKey(
                    name: "FK_RefreshToken_Staff_StaffId",
                    column: x => x.StaffId,
                    principalTable: "Staff",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "StaffPhoto",
            columns: table => new
            {
                StaffId = table.Column<int>(type: "int", nullable: false),
                Content = table.Column<byte[]>(type: "varbinary(max)", maxLength: 30720, nullable: false),
                ContentType = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                Width = table.Column<int>(type: "int", nullable: false),
                Height = table.Column<int>(type: "int", nullable: false),
                UpdatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StaffPhoto", x => x.StaffId);
                table.CheckConstraint("CK_StaffPhoto_Pixels", "[Width] BETWEEN 1 AND 256 AND [Height] BETWEEN 1 AND 256");
                table.CheckConstraint("CK_StaffPhoto_Size", "DATALENGTH([Content]) BETWEEN 1 AND 30720");
                table.ForeignKey(
                    name: "FK_StaffPhoto_Staff_StaffId",
                    column: x => x.StaffId,
                    principalTable: "Staff",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "Receipt",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                CreatedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()"),
                Kind = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                BloodType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                Units = table.Column<int>(type: "int", nullable: false),
                StaffId = table.Column<int>(type: "int", nullable: false),
                AppointmentId = table.Column<int>(type: "int", nullable: true),
                RequestId = table.Column<int>(type: "int", nullable: true),
                Notes = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Receipt", x => x.Id);
                table.CheckConstraint("CK_Receipt_BloodType", "[BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
                table.CheckConstraint("CK_Receipt_Kind", "[Kind] IN ('Donation', 'Issue')");
                table.CheckConstraint("CK_Receipt_Links", "([Kind] = 'Donation' AND [AppointmentId] IS NOT NULL AND [RequestId] IS NULL) OR ([Kind] = 'Issue' AND [RequestId] IS NOT NULL AND [AppointmentId] IS NULL)");
                table.CheckConstraint("CK_Receipt_Units", "[Units] > 0");
                table.ForeignKey(
                    name: "FK_Receipt_Appointment_AppointmentId",
                    column: x => x.AppointmentId,
                    principalTable: "Appointment",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Receipt_Request_RequestId",
                    column: x => x.RequestId,
                    principalTable: "Request",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_Receipt_Staff_StaffId",
                    column: x => x.StaffId,
                    principalTable: "Staff",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "BloodBag",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                BloodType = table.Column<string>(type: "varchar(5)", unicode: false, maxLength: 5, nullable: false),
                CollectedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                ExpiresOn = table.Column<DateOnly>(type: "date", nullable: false),
                Status = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                DonationReceiptId = table.Column<int>(type: "int", nullable: false),
                IssueReceiptId = table.Column<int>(type: "int", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_BloodBag", x => x.Id);
                table.CheckConstraint("CK_BloodBag_BloodType", "[BloodType] IN ('APos', 'ANeg', 'BPos', 'BNeg', 'ABPos', 'ABNeg', 'OPos', 'ONeg')");
                table.CheckConstraint("CK_BloodBag_IssueLink", "([Status] = 'Issued' AND [IssueReceiptId] IS NOT NULL) OR ([Status] <> 'Issued' AND [IssueReceiptId] IS NULL)");
                table.CheckConstraint("CK_BloodBag_Status", "[Status] IN ('Available', 'Issued', 'Discarded')");
                table.ForeignKey(
                    name: "FK_BloodBag_Receipt_DonationReceiptId",
                    column: x => x.DonationReceiptId,
                    principalTable: "Receipt",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_BloodBag_Receipt_IssueReceiptId",
                    column: x => x.IssueReceiptId,
                    principalTable: "Receipt",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

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

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "Status", "SlotStartUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLog_Entity_EntityId",
            table: "AuditLog",
            columns: new[] { "Entity", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLog_OccurredAtUtc",
            table: "AuditLog",
            column: "OccurredAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_AuditLog_StaffId",
            table: "AuditLog",
            column: "StaffId");

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_BloodType_Status_CollectedAtUtc",
            table: "BloodBag",
            columns: new[] { "BloodType", "Status", "CollectedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_CollectedAtUtc",
            table: "BloodBag",
            column: "CollectedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_DonationReceiptId",
            table: "BloodBag",
            column: "DonationReceiptId");

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_ExpiresOn_BloodType",
            table: "BloodBag",
            columns: new[] { "ExpiresOn", "BloodType" },
            filter: "[Status] = 'Available'");

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_IssueReceiptId",
            table: "BloodBag",
            column: "IssueReceiptId");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType",
            table: "Donor",
            column: "BloodType");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_Gender",
            table: "Donor",
            column: "Gender");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_LastDonated",
            table: "Donor",
            column: "LastDonated");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_Name",
            table: "Donor",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Patient_BloodType",
            table: "Patient",
            column: "BloodType");

        migrationBuilder.CreateIndex(
            name: "IX_Patient_Gender",
            table: "Patient",
            column: "Gender");

        migrationBuilder.CreateIndex(
            name: "IX_Patient_Name",
            table: "Patient",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_AppointmentId",
            table: "Receipt",
            column: "AppointmentId",
            unique: true,
            filter: "[AppointmentId] IS NOT NULL");

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_BloodType_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "BloodType", "CreatedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_CreatedAtUtc",
            table: "Receipt",
            column: "CreatedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "Kind", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "BloodType", "Units" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_RequestId",
            table: "Receipt",
            column: "RequestId");

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_StaffId",
            table: "Receipt",
            column: "StaffId");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshToken_ExpiresAtUtc",
            table: "RefreshToken",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshToken_FamilyId",
            table: "RefreshToken",
            column: "FamilyId");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshToken_StaffId",
            table: "RefreshToken",
            column: "StaffId");

        migrationBuilder.CreateIndex(
            name: "IX_RefreshToken_TokenHash",
            table: "RefreshToken",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_Status_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "Status", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId",
            table: "Request",
            column: "PatientId");

        migrationBuilder.CreateIndex(
            name: "IX_Request_PlacedAtUtc",
            table: "Request",
            column: "PlacedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_Request_RequiredBy",
            table: "Request",
            column: "RequiredBy");

        migrationBuilder.CreateIndex(
            name: "IX_Request_Status_PlacedAtUtc",
            table: "Request",
            columns: new[] { "Status", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_Status_RequiredBy",
            table: "Request",
            columns: new[] { "Status", "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Staff_DisplayName",
            table: "Staff",
            column: "DisplayName");

        migrationBuilder.CreateIndex(
            name: "IX_Staff_Username",
            table: "Staff",
            column: "Username",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "AuditLog");

        migrationBuilder.DropTable(
            name: "BloodBag");

        migrationBuilder.DropTable(
            name: "DemoState");

        migrationBuilder.DropTable(
            name: "RefreshToken");

        migrationBuilder.DropTable(
            name: "StaffPhoto");

        migrationBuilder.DropTable(
            name: "Receipt");

        migrationBuilder.DropTable(
            name: "Appointment");

        migrationBuilder.DropTable(
            name: "Request");

        migrationBuilder.DropTable(
            name: "Staff");

        migrationBuilder.DropTable(
            name: "Donor");

        migrationBuilder.DropTable(
            name: "Patient");
    }
}
