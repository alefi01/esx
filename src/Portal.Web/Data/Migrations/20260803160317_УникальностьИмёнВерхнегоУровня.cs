using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class УникальностьИмёнВерхнегоУровня : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Сначала разбираемся с тем, что уже есть в базе.
            //
            // Пока проверки на верхнем уровне не работало, две папки с одним
            // именем создать было можно. Если такие есть, создание уникального
            // индекса упадёт — а миграции применяются при запуске портала,
            // то есть портал просто не поднимется. Поэтому не падаем,
            // а переименовываем лишние: «Договоры» → «Договоры (2)».
            //
            // Первая по номеру папка имя сохраняет: она, скорее всего,
            // и есть та, которой пользуются.
            migrationBuilder.Sql("""
                UPDATE "Folders" AS f
                SET "Name" = f."Name" || ' (' || n."Номер" || ')'
                FROM (
                    SELECT "Id",
                           ROW_NUMBER() OVER (PARTITION BY lower("Name") ORDER BY "Id") AS "Номер"
                    FROM "Folders"
                    WHERE "ParentId" IS NULL
                ) AS n
                WHERE f."Id" = n."Id" AND n."Номер" > 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_Name_Root",
                table: "Folders",
                column: "Name",
                unique: true,
                filter: "\"ParentId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Folders_Name_Root",
                table: "Folders");
        }
    }
}
