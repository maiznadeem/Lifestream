using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifestream.Data.Migrations;

/// <inheritdoc />
public partial class StaffCredentialAndSecurityState : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "DemoRevocation",
            columns: table => new
            {
                Key = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false, collation: "Latin1_General_100_BIN2"),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_DemoRevocation", x => x.Key);
            });

        migrationBuilder.CreateTable(
            name: "StaffCredential",
            columns: table => new
            {
                StaffId = table.Column<int>(type: "int", nullable: false),
                PasswordHash = table.Column<string>(type: "varchar(256)", unicode: false, maxLength: 256, nullable: false),
                FailedCount = table.Column<int>(type: "int", nullable: false),
                LockoutEndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                MustChangePassword = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                SecurityStamp = table.Column<Guid>(type: "uniqueidentifier", nullable: false, defaultValueSql: "NEWID()"),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_StaffCredential", x => x.StaffId);
                table.CheckConstraint("CK_StaffCredential_PasswordHash", "LEN([PasswordHash]) BETWEEN 84 AND 256 AND LEN([PasswordHash]) % 4 = 0 AND [PasswordHash] NOT LIKE '%[^A-Za-z0-9+/=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%=%[^=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%===%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] LIKE 'AQAAAAIA____AAAAE%' COLLATE Latin1_General_100_BIN2");
                table.ForeignKey(
                    name: "FK_StaffCredential_Staff_StaffId",
                    column: x => x.StaffId,
                    principalTable: "Staff",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "UnknownLogin",
            columns: table => new
            {
                Id = table.Column<int>(type: "int", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false, collation: "Latin1_General_100_CI_AS"),
                FailedCount = table.Column<int>(type: "int", nullable: false),
                LockoutEndUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: true),
                ExpiresAtUtc = table.Column<DateTime>(type: "datetime2(3)", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UnknownLogin", x => x.Id);
            });

        // Move every existing credential across before the columns go: nothing is lost and nobody has to sign in again.
        // (EXEC: the idempotent script must still compile on a database where the old columns are already gone.)
        migrationBuilder.Sql(
            "EXEC(N'INSERT INTO [StaffCredential] ([StaffId], [PasswordHash], [FailedCount], [LockoutEndUtc], [MustChangePassword]) " +
            "SELECT [Id], [PasswordHash], [FailedLoginCount], [LockoutEndUtc], [MustChangePassword] FROM [Staff]');");

        migrationBuilder.DropCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff");

        migrationBuilder.DropColumn(
            name: "FailedLoginCount",
            table: "Staff");

        migrationBuilder.DropColumn(
            name: "LockoutEndUtc",
            table: "Staff");

        migrationBuilder.DropColumn(
            name: "MustChangePassword",
            table: "Staff");

        migrationBuilder.DropColumn(
            name: "PasswordHash",
            table: "Staff");

        migrationBuilder.CreateIndex(
            name: "IX_DemoRevocation_ExpiresAtUtc",
            table: "DemoRevocation",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_UnknownLogin_ExpiresAtUtc",
            table: "UnknownLogin",
            column: "ExpiresAtUtc");

        migrationBuilder.CreateIndex(
            name: "IX_UnknownLogin_Name",
            table: "UnknownLogin",
            column: "Name",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "FailedLoginCount",
            table: "Staff",
            type: "int",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTime>(
            name: "LockoutEndUtc",
            table: "Staff",
            type: "datetime2(3)",
            nullable: true);

        migrationBuilder.AddColumn<bool>(
            name: "MustChangePassword",
            table: "Staff",
            type: "bit",
            nullable: false,
            defaultValue: false);

        migrationBuilder.AddColumn<string>(
            name: "PasswordHash",
            table: "Staff",
            type: "varchar(256)",
            unicode: false,
            maxLength: 256,
            nullable: false,
            defaultValue: "");

        migrationBuilder.Sql(
            "EXEC(N'UPDATE s SET s.[PasswordHash] = c.[PasswordHash], s.[FailedLoginCount] = c.[FailedCount], s.[LockoutEndUtc] = c.[LockoutEndUtc], s.[MustChangePassword] = c.[MustChangePassword] " +
            "FROM [Staff] s JOIN [StaffCredential] c ON c.[StaffId] = s.[Id]');");

        migrationBuilder.AddCheckConstraint(
            name: "CK_Staff_PasswordHash",
            table: "Staff",
            sql: "LEN([PasswordHash]) BETWEEN 84 AND 256 AND LEN([PasswordHash]) % 4 = 0 AND [PasswordHash] NOT LIKE '%[^A-Za-z0-9+/=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%=%[^=]%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] NOT LIKE '%===%' COLLATE Latin1_General_100_BIN2 AND [PasswordHash] LIKE 'AQAAAAIA____AAAAE%' COLLATE Latin1_General_100_BIN2");

        migrationBuilder.DropTable(
            name: "DemoRevocation");

        migrationBuilder.DropTable(
            name: "StaffCredential");

        migrationBuilder.DropTable(
            name: "UnknownLogin");
    }
}
