using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class НесколькоИсполнителейИПравкаЗадач : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                table: "Tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "EditedAt",
                table: "AssignedTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssigneeDisplayName",
                table: "AssignedTaskInstances",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "AssigneeUserName",
                table: "AssignedTaskInstances",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "AssignedTaskAssignees",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IsController = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignedTaskAssignees", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignedTaskAssignees_AssignedTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AssignedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskInstances_AssigneeUserName",
                table: "AssignedTaskInstances",
                column: "AssigneeUserName");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskAssignees_TaskId_UserName",
                table: "AssignedTaskAssignees",
                columns: new[] { "TaskId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskAssignees_UserName",
                table: "AssignedTaskAssignees",
                column: "UserName");

            // Переносим старые задачи: до этой правки исполнитель был один
            // и хранился в самой задаче. Теперь исполнители — отдельной
            // таблицей, а у каждого экземпляра свой исполнитель.
            //
            // Без переноса старые задачи пропали бы из списков: они ищут
            // по новым полям.
            migrationBuilder.Sql("""
                INSERT INTO "AssignedTaskAssignees" ("TaskId", "UserName", "DisplayName", "IsController")
                SELECT "Id", "AssigneeUserName", "AssigneeDisplayName", false
                FROM "AssignedTasks"
                WHERE "AssigneeUserName" <> '';
                """);

            migrationBuilder.Sql("""
                UPDATE "AssignedTaskInstances" AS i
                SET "AssigneeUserName" = t."AssigneeUserName",
                    "AssigneeDisplayName" = t."AssigneeDisplayName"
                FROM "AssignedTasks" AS t
                WHERE i."TaskId" = t."Id" AND i."AssigneeUserName" = '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AssignedTaskAssignees");

            migrationBuilder.DropIndex(
                name: "IX_AssignedTaskInstances_AssigneeUserName",
                table: "AssignedTaskInstances");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "EditedAt",
                table: "AssignedTasks");

            migrationBuilder.DropColumn(
                name: "AssigneeDisplayName",
                table: "AssignedTaskInstances");

            migrationBuilder.DropColumn(
                name: "AssigneeUserName",
                table: "AssignedTaskInstances");
        }
    }
}
