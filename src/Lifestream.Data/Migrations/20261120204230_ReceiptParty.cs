using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class _20261120204230_ReceiptParty : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Receipt_Appointment_AppointmentId",
            table: "Receipt");

        migrationBuilder.DropForeignKey(
            name: "FK_Receipt_Request_RequestId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_AppointmentId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_BloodType_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_Kind_BloodType_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_RequestId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_StaffId_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.AddColumn<int>(
            name: "DonorId",
            table: "Receipt",
            type: "int",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "PatientId",
            table: "Receipt",
            type: "int",
            nullable: true);

        // Existing receipts take their party from their appointment (the donor) or their request (the patient), before the check constraint and
        // the foreign keys below require it. Every receipt has exactly one of the two (CK_Receipt_Links), so none is left without. EXEC defers
        // name resolution, so the idempotent script (one batch with the AddColumn above) compiles.
        migrationBuilder.Sql(
            "EXEC(N'UPDATE r SET r.DonorId = a.DonorId FROM Receipt r JOIN Appointment a ON a.Id = r.AppointmentId WHERE r.DonorId IS NULL')");
        migrationBuilder.Sql(
            "EXEC(N'UPDATE r SET r.PatientId = q.PatientId FROM Receipt r JOIN Request q ON q.Id = r.RequestId WHERE r.PatientId IS NULL')");

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Request_Id_PatientId",
            table: "Request",
            columns: new[] { "Id", "PatientId" });

        migrationBuilder.AddUniqueConstraint(
            name: "AK_Appointment_Id_DonorId",
            table: "Appointment",
            columns: new[] { "Id", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_AppointmentId_DonorId",
            table: "Receipt",
            columns: new[] { "AppointmentId", "DonorId" },
            unique: true,
            filter: "[AppointmentId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "CreatedAtUtc", "Kind", "BloodType" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_BloodType_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "BloodType", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Kind", "Units", "StaffId", "AppointmentId", "RequestId", "DonorId", "PatientId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_CreatedAtUtc",
            table: "Receipt",
            column: "CreatedAtUtc")
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType", "Units", "StaffId", "AppointmentId", "RequestId", "DonorId", "PatientId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_DonorId_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "DonorId", "CreatedAtUtc" },
            filter: "[DonorId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType", "StaffId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_Kind_BloodType_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "Kind", "BloodType", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Units", "StaffId", "AppointmentId", "RequestId", "DonorId", "PatientId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "Kind", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "BloodType", "Units", "StaffId", "AppointmentId", "RequestId", "DonorId", "PatientId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_PatientId_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "PatientId", "CreatedAtUtc" },
            filter: "[PatientId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType", "StaffId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_RequestId_PatientId",
            table: "Receipt",
            columns: new[] { "RequestId", "PatientId" },
            filter: "[RequestId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "CreatedAtUtc", "Kind", "BloodType" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_StaffId_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "StaffId", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType", "DonorId", "PatientId" });

        migrationBuilder.AddCheckConstraint(
            name: "CK_Receipt_Party",
            table: "Receipt",
            sql: "([Kind] = 'Donation' AND [DonorId] IS NOT NULL AND [PatientId] IS NULL) OR ([Kind] = 'Issue' AND [PatientId] IS NOT NULL AND [DonorId] IS NULL)");

        migrationBuilder.AddForeignKey(
            name: "FK_Receipt_Appointment_AppointmentId_DonorId",
            table: "Receipt",
            columns: new[] { "AppointmentId", "DonorId" },
            principalTable: "Appointment",
            principalColumns: new[] { "Id", "DonorId" },
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Receipt_Request_RequestId_PatientId",
            table: "Receipt",
            columns: new[] { "RequestId", "PatientId" },
            principalTable: "Request",
            principalColumns: new[] { "Id", "PatientId" },
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Receipt_Appointment_AppointmentId_DonorId",
            table: "Receipt");

        migrationBuilder.DropForeignKey(
            name: "FK_Receipt_Request_RequestId_PatientId",
            table: "Receipt");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_Request_Id_PatientId",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_AppointmentId_DonorId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_BloodType_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_DonorId_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_Kind_BloodType_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_PatientId_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_RequestId_PatientId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_StaffId_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Receipt_Party",
            table: "Receipt");

        migrationBuilder.DropUniqueConstraint(
            name: "AK_Appointment_Id_DonorId",
            table: "Appointment");

        migrationBuilder.DropColumn(
            name: "DonorId",
            table: "Receipt");

        migrationBuilder.DropColumn(
            name: "PatientId",
            table: "Receipt");

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_AppointmentId",
            table: "Receipt",
            column: "AppointmentId",
            unique: true,
            filter: "[AppointmentId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "CreatedAtUtc", "Kind", "BloodType" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_BloodType_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "BloodType", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Kind", "Units", "StaffId", "AppointmentId", "RequestId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_CreatedAtUtc",
            table: "Receipt",
            column: "CreatedAtUtc")
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType", "Units", "StaffId", "AppointmentId", "RequestId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_Kind_BloodType_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "Kind", "BloodType", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Units", "StaffId", "AppointmentId", "RequestId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "Kind", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "BloodType", "Units", "StaffId", "AppointmentId", "RequestId" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_RequestId",
            table: "Receipt",
            column: "RequestId",
            filter: "[RequestId] IS NOT NULL")
            .Annotation("SqlServer:Include", new[] { "CreatedAtUtc", "Kind", "BloodType" });

        migrationBuilder.CreateIndex(
            name: "IX_Receipt_StaffId_CreatedAtUtc",
            table: "Receipt",
            columns: new[] { "StaffId", "CreatedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "Kind", "BloodType" });

        migrationBuilder.AddForeignKey(
            name: "FK_Receipt_Appointment_AppointmentId",
            table: "Receipt",
            column: "AppointmentId",
            principalTable: "Appointment",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);

        migrationBuilder.AddForeignKey(
            name: "FK_Receipt_Request_RequestId",
            table: "Receipt",
            column: "RequestId",
            principalTable: "Request",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }
}
