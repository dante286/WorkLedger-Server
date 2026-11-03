using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkLedger.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPasswordResetSelfService : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SelfService",
                table: "PasswordResets",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SelfService",
                table: "PasswordResets");
        }
    }
}
