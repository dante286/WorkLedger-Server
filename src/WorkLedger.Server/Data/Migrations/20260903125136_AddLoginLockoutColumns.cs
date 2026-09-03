using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkLedger.Server.Data.Migrations
{
    /// <summary>
    /// S7: the three <c>Users</c> columns login writes — the brute-force counter, the lockout deadline,
    /// and a last-login stamp. Scaffolded as EF produced it; nothing here is hand-edited.
    /// <para>
    /// <c>FailedLoginCount</c> is non-null and arrives with <c>defaultValue: 0</c> purely to backfill
    /// rows that already exist. The EF model declares no default, which is deliberate and harmless: the
    /// column default is never consulted, because every write goes through the entity and always carries
    /// a value. Unlike S6's <c>SecurityStamp</c> — where the backfill had to be a distinct value per row
    /// and so could not be a column default at all — zero is genuinely the right starting count for
    /// every existing user.
    /// </para>
    /// </summary>
    public partial class AddLoginLockoutColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                table: "Users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastLoginUtc",
                table: "Users",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LockoutEndUtc",
                table: "Users",
                type: "timestamptz",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastLoginUtc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LockoutEndUtc",
                table: "Users");
        }
    }
}
