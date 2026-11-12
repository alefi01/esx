using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class НесколькоЗакреплённыхСообщений : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsPinned",
                table: "Messages",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PinnedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: true);

            // Переносим то, что уже закреплено: раньше закреплённое
            // сообщение было одно и хранилось у беседы. Потерять его
            // при обновлении нельзя — его закрепили, чтобы видели все.
            migrationBuilder.Sql("""
                UPDATE "Messages" AS m
                SET "IsPinned" = true, "PinnedAt" = m."CreatedAt"
                FROM "Conversations" AS c
                WHERE c."PinnedMessageId" = m."Id";
                """);

            migrationBuilder.DropColumn(
                name: "PinnedMessageId",
                table: "Conversations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsPinned",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "PinnedAt",
                table: "Messages");

            migrationBuilder.AddColumn<int>(
                name: "PinnedMessageId",
                table: "Conversations",
                type: "integer",
                nullable: true);
        }
    }
}
