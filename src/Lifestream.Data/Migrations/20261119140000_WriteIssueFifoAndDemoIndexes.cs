using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class WriteIssueFifoAndDemoIndexes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateIndex(
            name: "IX_Staff_IsDemo",
            table: "Staff",
            column: "IsDemo",
            filter: "[IsDemo] = 1")
            .Annotation("SqlServer:Include", new[] { "DisplayName" });

        migrationBuilder.CreateIndex(
            name: "IX_BloodBag_BloodType_ExpiresOn",
            table: "BloodBag",
            columns: new[] { "BloodType", "ExpiresOn" },
            filter: "[Status] = 'Available'")
            .Annotation("SqlServer:Include", new[] { "CollectedAtUtc" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_Staff_IsDemo",
            table: "Staff");

        migrationBuilder.DropIndex(
            name: "IX_BloodBag_BloodType_ExpiresOn",
            table: "BloodBag");
    }
}
