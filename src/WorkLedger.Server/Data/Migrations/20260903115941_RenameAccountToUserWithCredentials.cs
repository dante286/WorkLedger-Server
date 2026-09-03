using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkLedger.Server.Data.Migrations
{
    /// <summary>
    /// S6: <c>Accounts</c> becomes <c>Users</c> and gains email/password credentials.
    /// <para>
    /// <b>Hand-edited, deliberately.</b> EF scaffolded this as <c>DropTable("Accounts")</c> +
    /// <c>CreateTable("Users")</c> — it cannot tell a rename from a coincidence of shape. Applied as
    /// scaffolded against a live self-hosted deployment it would have deleted the one account row and
    /// taken every project, task, time entry and enrolled device with it through the cascading foreign
    /// keys. Every operation below is therefore a rename, and the data is never copied or re-created.
    /// </para>
    /// <para>
    /// Postgres keeps constraint and index names across <c>ALTER TABLE ... RENAME</c>, so the PK and the
    /// four foreign keys would otherwise still be called <c>PK_Accounts</c> and
    /// <c>FK_&lt;table&gt;_Accounts_AccountId</c> while EF's model expects the <c>_Users_UserId</c> names.
    /// The mismatch is invisible until some later migration tries to drop one by its conventional name,
    /// which is why they are renamed here rather than left to be discovered.
    /// </para>
    /// <para>
    /// <c>SecurityStamp</c> is non-null in the model but has no value on rows that already exist, so it
    /// arrives nullable, gets a per-row UUID, and is only then tightened. Doing it that way instead of
    /// EF's usual <c>defaultValue: ""</c> leaves no column default behind, so a database that migrated
    /// into this schema and one created fresh through it are byte-identical.
    /// </para>
    /// </summary>
    public partial class RenameAccountToUserWithCredentials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameTable(
                name: "Accounts",
                newName: "Users");

            migrationBuilder.RenameColumn(
                name: "Name",
                table: "Users",
                newName: "DisplayName");

            // Account.Name was required; User.DisplayName is optional and free-form — nothing keys off it.
            migrationBuilder.AlterColumn<string>(
                name: "DisplayName",
                table: "Users",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PasswordHash",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Users",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "text",
                nullable: true);

            migrationBuilder.Sql(
                @"UPDATE ""Users"" SET ""SecurityStamp"" = gen_random_uuid()::text WHERE ""SecurityStamp"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "SecurityStamp",
                table: "Users",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            // Partial, so the pre-S6 rows this migration leaves with a null email do not collide.
            migrationBuilder.CreateIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users",
                column: "NormalizedEmail",
                unique: true,
                filter: "\"NormalizedEmail\" IS NOT NULL");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "Devices",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_Devices_AccountId_DeviceId",
                table: "Devices",
                newName: "IX_Devices_UserId_DeviceId");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "Projects",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_AccountId_SyncId",
                table: "Projects",
                newName: "IX_Projects_UserId_SyncId");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_AccountId_UpdatedAt",
                table: "Projects",
                newName: "IX_Projects_UserId_UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "TaskItems",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_AccountId_ProjectSyncId",
                table: "TaskItems",
                newName: "IX_TaskItems_UserId_ProjectSyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_AccountId_SyncId",
                table: "TaskItems",
                newName: "IX_TaskItems_UserId_SyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_AccountId_UpdatedAt",
                table: "TaskItems",
                newName: "IX_TaskItems_UserId_UpdatedAt");

            migrationBuilder.RenameColumn(
                name: "AccountId",
                table: "TimeEntries",
                newName: "UserId");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_AccountId_SyncId",
                table: "TimeEntries",
                newName: "IX_TimeEntries_UserId_SyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_AccountId_TaskSyncId",
                table: "TimeEntries",
                newName: "IX_TimeEntries_UserId_TaskSyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_AccountId_UpdatedAt",
                table: "TimeEntries",
                newName: "IX_TimeEntries_UserId_UpdatedAt");

            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" RENAME CONSTRAINT ""PK_Accounts"" TO ""PK_Users"";
                ALTER TABLE ""Devices"" RENAME CONSTRAINT ""FK_Devices_Accounts_AccountId"" TO ""FK_Devices_Users_UserId"";
                ALTER TABLE ""Projects"" RENAME CONSTRAINT ""FK_Projects_Accounts_AccountId"" TO ""FK_Projects_Users_UserId"";
                ALTER TABLE ""TaskItems"" RENAME CONSTRAINT ""FK_TaskItems_Accounts_AccountId"" TO ""FK_TaskItems_Users_UserId"";
                ALTER TABLE ""TimeEntries"" RENAME CONSTRAINT ""FK_TimeEntries_Accounts_AccountId"" TO ""FK_TimeEntries_Users_UserId"";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" RENAME CONSTRAINT ""PK_Users"" TO ""PK_Accounts"";
                ALTER TABLE ""Devices"" RENAME CONSTRAINT ""FK_Devices_Users_UserId"" TO ""FK_Devices_Accounts_AccountId"";
                ALTER TABLE ""Projects"" RENAME CONSTRAINT ""FK_Projects_Users_UserId"" TO ""FK_Projects_Accounts_AccountId"";
                ALTER TABLE ""TaskItems"" RENAME CONSTRAINT ""FK_TaskItems_Users_UserId"" TO ""FK_TaskItems_Accounts_AccountId"";
                ALTER TABLE ""TimeEntries"" RENAME CONSTRAINT ""FK_TimeEntries_Users_UserId"" TO ""FK_TimeEntries_Accounts_AccountId"";");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_UserId_UpdatedAt",
                table: "TimeEntries",
                newName: "IX_TimeEntries_AccountId_UpdatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_UserId_TaskSyncId",
                table: "TimeEntries",
                newName: "IX_TimeEntries_AccountId_TaskSyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TimeEntries_UserId_SyncId",
                table: "TimeEntries",
                newName: "IX_TimeEntries_AccountId_SyncId");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "TimeEntries",
                newName: "AccountId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_UserId_UpdatedAt",
                table: "TaskItems",
                newName: "IX_TaskItems_AccountId_UpdatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_UserId_SyncId",
                table: "TaskItems",
                newName: "IX_TaskItems_AccountId_SyncId");

            migrationBuilder.RenameIndex(
                name: "IX_TaskItems_UserId_ProjectSyncId",
                table: "TaskItems",
                newName: "IX_TaskItems_AccountId_ProjectSyncId");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "TaskItems",
                newName: "AccountId");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_UserId_UpdatedAt",
                table: "Projects",
                newName: "IX_Projects_AccountId_UpdatedAt");

            migrationBuilder.RenameIndex(
                name: "IX_Projects_UserId_SyncId",
                table: "Projects",
                newName: "IX_Projects_AccountId_SyncId");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "Projects",
                newName: "AccountId");

            migrationBuilder.RenameIndex(
                name: "IX_Devices_UserId_DeviceId",
                table: "Devices",
                newName: "IX_Devices_AccountId_DeviceId");

            migrationBuilder.RenameColumn(
                name: "UserId",
                table: "Devices",
                newName: "AccountId");

            migrationBuilder.DropIndex(
                name: "IX_Users_NormalizedEmail",
                table: "Users");

            migrationBuilder.DropColumn(name: "SecurityStamp", table: "Users");
            migrationBuilder.DropColumn(name: "UpdatedAt", table: "Users");
            migrationBuilder.DropColumn(name: "PasswordHash", table: "Users");
            migrationBuilder.DropColumn(name: "NormalizedEmail", table: "Users");
            migrationBuilder.DropColumn(name: "Email", table: "Users");

            // Account.Name was required, so a claimed-but-unnamed user needs a value before the column
            // can be tightened again. Rolling back loses the credentials, which is the point of a
            // rollback; it must not also fail on a NOT NULL it can satisfy.
            migrationBuilder.Sql(@"UPDATE ""Users"" SET ""DisplayName"" = '' WHERE ""DisplayName"" IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "DisplayName",
                table: "Users",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.RenameColumn(
                name: "DisplayName",
                table: "Users",
                newName: "Name");

            migrationBuilder.RenameTable(
                name: "Users",
                newName: "Accounts");
        }
    }
}
