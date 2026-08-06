using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class СинхронизацияФилиалов : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAt",
                table: "Messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "Messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginBranch",
                table: "Messages",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "MessageFiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAt",
                table: "Folders",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "Folders",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginBranch",
                table: "Folders",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAt",
                table: "Files",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "Files",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginBranch",
                table: "Files",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAt",
                table: "Conversations",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "Conversations",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginBranch",
                table: "Conversations",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "ChangedAt",
                table: "Announcements",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "Announcements",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "OriginBranch",
                table: "Announcements",
                type: "character varying(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "GlobalId",
                table: "AnnouncementFiles",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "SyncOutbox",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    GlobalId = table.Column<Guid>(type: "uuid", nullable: false),
                    OriginBranch = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Deleted = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncOutbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SyncPeers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PeerCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    LastReceivedId = table.Column<long>(type: "bigint", nullable: false),
                    LastSuccessAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastAttemptAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastReceivedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SyncPeers", x => x.Id);
                });

            // ------------------------------------------------------------------
            // Общие номера для того, что уже лежит в базе.
            //
            // Колонка добавлена со значением по умолчанию, а по умолчанию
            // это «пустой» номер 00000000-0000-…, ОДИНАКОВЫЙ у всех строк.
            // Индекс ниже требует уникальности, и без этого шага миграция
            // упала бы на второй же строке любой непустой таблицы.
            //
            // gen_random_uuid() — встроенная в PostgreSQL функция
            // (начиная с версии 13), расширений ставить не нужно.
            // ------------------------------------------------------------------

            foreach (var table in new[]
                     {
                         "Announcements", "AnnouncementFiles", "Folders",
                         "Files", "Conversations", "Messages", "MessageFiles"
                     })
            {
                migrationBuilder.Sql(
                    $"""UPDATE "{table}" SET "GlobalId" = gen_random_uuid() WHERE "GlobalId" = '00000000-0000-0000-0000-000000000000';""");
            }

            // Время последнего изменения у старых строк нулевое (0001-01-01).
            // Ставим текущее: иначе любая правка того же объекта в соседнем
            // филиале всегда считалась бы новее — даже сделанная год назад.
            foreach (var table in new[] { "Announcements", "Folders", "Files", "Conversations", "Messages" })
            {
                migrationBuilder.Sql(
                    $"""UPDATE "{table}" SET "ChangedAt" = NOW() AT TIME ZONE 'UTC' WHERE "ChangedAt" < '2000-01-01';""");
            }

            // ------------------------------------------------------------------
            // Всё, что уже есть, — в журнал изменений.
            //
            // Иначе получилось бы вот что: в филиалах ставят новые серверы
            // с пустыми базами, включают обмен — а в журнале нет НИ ОДНОЙ
            // записи, потому что журнал заполняется только при изменениях.
            // Соседи спросили бы «что нового?», получили «ничего»
            // и остались бы пустыми навсегда.
            //
            // Поэтому при переходе на филиальную схему всё имеющееся
            // объявляется изменённым один раз. Дальше журнал наполняется
            // сам, обычным порядком.
            //
            // Порядок вставки важен: папки раньше файлов, беседы раньше
            // сообщений. Иначе первый обмен пойдёт с откладыванием записей
            // и потратит лишние проходы. Потеряться при этом ничего
            // не может (см. SyncApplier), но ждать пришлось бы дольше.
            // ------------------------------------------------------------------

            foreach (var (table, kind) in new[]
                     {
                         ("Announcements", "announcement"),
                         ("Folders", "folder"),
                         ("Files", "file"),
                         ("Conversations", "conversation"),
                         ("Messages", "message")
                     })
            {
                migrationBuilder.Sql(
                    $"""
                     INSERT INTO "SyncOutbox" ("Kind", "GlobalId", "OriginBranch", "ChangedAt", "Deleted")
                     SELECT '{kind}', "GlobalId", "OriginBranch", NOW() AT TIME ZONE 'UTC', FALSE FROM "{table}";
                     """);
            }

            migrationBuilder.CreateIndex(
                name: "IX_Messages_GlobalId",
                table: "Messages",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MessageFiles_GlobalId",
                table: "MessageFiles",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Folders_GlobalId",
                table: "Folders",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Files_GlobalId",
                table: "Files",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_GlobalId",
                table: "Conversations",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_GlobalId",
                table: "Announcements",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AnnouncementFiles_GlobalId",
                table: "AnnouncementFiles",
                column: "GlobalId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_ChangedAt",
                table: "SyncOutbox",
                column: "ChangedAt");

            migrationBuilder.CreateIndex(
                name: "IX_SyncOutbox_Id",
                table: "SyncOutbox",
                column: "Id");

            migrationBuilder.CreateIndex(
                name: "IX_SyncPeers_PeerCode",
                table: "SyncPeers",
                column: "PeerCode",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SyncOutbox");

            migrationBuilder.DropTable(
                name: "SyncPeers");

            migrationBuilder.DropIndex(
                name: "IX_Messages_GlobalId",
                table: "Messages");

            migrationBuilder.DropIndex(
                name: "IX_MessageFiles_GlobalId",
                table: "MessageFiles");

            migrationBuilder.DropIndex(
                name: "IX_Folders_GlobalId",
                table: "Folders");

            migrationBuilder.DropIndex(
                name: "IX_Files_GlobalId",
                table: "Files");

            migrationBuilder.DropIndex(
                name: "IX_Conversations_GlobalId",
                table: "Conversations");

            migrationBuilder.DropIndex(
                name: "IX_Announcements_GlobalId",
                table: "Announcements");

            migrationBuilder.DropIndex(
                name: "IX_AnnouncementFiles_GlobalId",
                table: "AnnouncementFiles");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "OriginBranch",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "MessageFiles");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "OriginBranch",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "OriginBranch",
                table: "Files");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "OriginBranch",
                table: "Conversations");

            migrationBuilder.DropColumn(
                name: "ChangedAt",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "OriginBranch",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "GlobalId",
                table: "AnnouncementFiles");
        }
    }
}
