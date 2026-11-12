using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ОтметкаВзятияЗадачиВРаботу : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TakenAt",
                table: "AssignedTaskInstances",
                type: "timestamp with time zone",
                nullable: true);

            // Задачи, которые уже лежали у людей, не объявляем новыми:
            // отметку вводим сегодня, а не задним числом. Иначе наутро
            // весь список у каждого оказался бы в столбце «Новые».
            migrationBuilder.Sql("""
                UPDATE "AssignedTaskInstances"
                SET "TakenAt" = "CreatedAt"
                WHERE "TakenAt" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TakenAt",
                table: "AssignedTaskInstances");
        }
    }
}
