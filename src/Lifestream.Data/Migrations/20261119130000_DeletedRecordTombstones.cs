using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class DeletedRecordTombstones : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DeletedRecord",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Entity = table.Column<string>(type: "varchar(30)", unicode: false, maxLength: 30, nullable: false),
                EntityId = table.Column<int>(type: "int", nullable: false),
                Json = table.Column<string>(type: "nvarchar(max)", nullable: false),
                DeletedAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false, defaultValueSql: "SYSUTCDATETIME()")
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DeletedRecord", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_DeletedRecord_DeletedAtUtc",
            table: "DeletedRecord",
            column: "DeletedAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_DeletedRecord_Entity_EntityId",
            table: "DeletedRecord",
            columns: new[] { "Entity", "EntityId" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "DeletedRecord");
    }
}
