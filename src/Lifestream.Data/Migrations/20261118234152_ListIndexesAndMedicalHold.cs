using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class _20261118234152_ListIndexesAndMedicalHold : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_Status_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_Status_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_Status_RequiredBy",
            table: "Request");

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
            name: "IX_Receipt_Kind_CreatedAtUtc",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Receipt_RequestId",
            table: "Receipt");

        migrationBuilder.DropIndex(
            name: "IX_Patient_BloodType_Name",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Patient_Name",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_LastDonated",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_Name",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
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

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.AddColumn<bool>(
            name: "MedicalHold",
            table: "Donor",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "Units", "RequiredBy", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request",
            columns: new[] { "BloodType", "RequiredBy" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "Units", "PlacedAtUtc", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_Status_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "Status", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "Units", "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_Status_RequiredBy",
            table: "Request",
            columns: new[] { "BloodType", "Status", "RequiredBy" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "Units", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId",
            table: "Request",
            column: "PatientId");

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request",
            columns: new[] { "PatientId", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "RequiredBy", "Status", "BloodType", "Units" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_PlacedAtUtc",
            table: "Request",
            column: "PlacedAtUtc")
            .Annotation("SqlServer:Include", new[] { "PatientId", "BloodType", "Units", "RequiredBy", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_RequiredBy",
            table: "Request",
            column: "RequiredBy")
            .Annotation("SqlServer:Include", new[] { "PatientId", "BloodType", "Units", "PlacedAtUtc", "Status" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_Status_PlacedAtUtc",
            table: "Request",
            columns: new[] { "Status", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "BloodType", "Units", "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_Status_RequiredBy",
            table: "Request",
            columns: new[] { "Status", "RequiredBy" })
            .Annotation("SqlServer:Include", new[] { "PatientId", "BloodType", "Units", "PlacedAtUtc" });

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

        migrationBuilder.CreateIndex(
            name: "IX_Patient_BloodType_Name",
            table: "Patient",
            columns: new[] { "BloodType", "Name" })
            .Annotation("SqlServer:Include", new[] { "DateOfBirth", "Gender", "Phone" });

        migrationBuilder.CreateIndex(
            name: "IX_Patient_Name",
            table: "Patient",
            column: "Name")
            .Annotation("SqlServer:Include", new[] { "DateOfBirth", "Gender", "BloodType", "Phone" });

        migrationBuilder.CreateIndex(
            name: "IX_Patient_Phone",
            table: "Patient",
            column: "Phone");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType_LastDonated",
            table: "Donor",
            columns: new[] { "BloodType", "LastDonated" })
            .Annotation("SqlServer:Include", new[] { "Name", "DateOfBirth", "Gender", "Phone", "Frequency", "MedicalHold" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor",
            columns: new[] { "BloodType", "Name" })
            .Annotation("SqlServer:Include", new[] { "DateOfBirth", "Gender", "Phone", "Frequency", "LastDonated", "MedicalHold" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_LastDonated",
            table: "Donor",
            column: "LastDonated")
            .Annotation("SqlServer:Include", new[] { "Name", "DateOfBirth", "Gender", "BloodType", "Phone", "Frequency", "MedicalHold" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_Name",
            table: "Donor",
            column: "Name")
            .Annotation("SqlServer:Include", new[] { "DateOfBirth", "Gender", "BloodType", "Phone", "Frequency", "LastDonated", "MedicalHold" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_Phone",
            table: "Donor",
            column: "Phone");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "SlotStartUtc", "Status", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "SlotStartUtc" })
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "Status", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorId",
            table: "Appointment",
            column: "DonorId");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment",
            column: "PlacedAtUtc")
            .Annotation("SqlServer:Include", new[] { "SlotStartUtc", "Status", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment",
            column: "SlotStartUtc")
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "Status", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_DonorBloodType_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "Status", "DonorBloodType", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "SlotStartUtc", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_DonorBloodType_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "Status", "DonorBloodType", "SlotStartUtc" })
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "Status", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "SlotStartUtc", "DonorId" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "Status", "SlotStartUtc" })
            .Annotation("SqlServer:Include", new[] { "PlacedAtUtc", "DonorId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_Status_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_BloodType_Status_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PatientId",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_RequiredBy",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_Status_PlacedAtUtc",
            table: "Request");

        migrationBuilder.DropIndex(
            name: "IX_Request_Status_RequiredBy",
            table: "Request");

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

        migrationBuilder.DropIndex(
            name: "IX_Patient_BloodType_Name",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Patient_Name",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Patient_Phone",
            table: "Patient");

        migrationBuilder.DropIndex(
            name: "IX_Donor_BloodType_LastDonated",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_LastDonated",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_Name",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Donor_Phone",
            table: "Donor");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_DonorId",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_DonorBloodType_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_DonorBloodType_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_PlacedAtUtc",
            table: "Appointment");

        migrationBuilder.DropIndex(
            name: "IX_Appointment_Status_SlotStartUtc",
            table: "Appointment");

        migrationBuilder.DropColumn(
            name: "MedicalHold",
            table: "Donor");

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_RequiredBy",
            table: "Request",
            columns: new[] { "BloodType", "RequiredBy" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_BloodType_Status_PlacedAtUtc",
            table: "Request",
            columns: new[] { "BloodType", "Status", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Request_PatientId_PlacedAtUtc",
            table: "Request",
            columns: new[] { "PatientId", "PlacedAtUtc" })
            .Annotation("SqlServer:Include", new[] { "RequiredBy" });

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
            name: "IX_Patient_BloodType_Name",
            table: "Patient",
            columns: new[] { "BloodType", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_Patient_Name",
            table: "Patient",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_BloodType_Name",
            table: "Donor",
            columns: new[] { "BloodType", "Name" });

        migrationBuilder.CreateIndex(
            name: "IX_Donor_LastDonated",
            table: "Donor",
            column: "LastDonated");

        migrationBuilder.CreateIndex(
            name: "IX_Donor_Name",
            table: "Donor",
            column: "Name");

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_PlacedAtUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "PlacedAtUtc" });

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_DonorBloodType_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "DonorBloodType", "SlotStartUtc" });

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

        migrationBuilder.CreateIndex(
            name: "IX_Appointment_Status_SlotStartUtc",
            table: "Appointment",
            columns: new[] { "Status", "SlotStartUtc" });
    }
}
