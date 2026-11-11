using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ОтделыОбъявленияВПапкахИЗадачи : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsHead",
                table: "SeenStates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "DepartmentRole",
                table: "Folders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDepartment",
                table: "Folders",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "FolderId",
                table: "Announcements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AssignedTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AuthorUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AuthorDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AssigneeUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AssigneeDisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Notes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Repeat = table.Column<int>(type: "integer", nullable: false),
                    WeekDays = table.Column<int>(type: "integer", nullable: false),
                    MonthDay = table.Column<int>(type: "integer", nullable: false),
                    DueMinutes = table.Column<int>(type: "integer", nullable: true),
                    NeedFile = table.Column<bool>(type: "boolean", nullable: false),
                    NeedCheck = table.Column<bool>(type: "boolean", nullable: false),
                    Important = table.Column<bool>(type: "boolean", nullable: false),
                    RemindMinutes = table.Column<int>(type: "integer", nullable: false),
                    Stopped = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignedTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FolderHeads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AssignedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderHeads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderHeads_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "FolderSeenStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    FolderId = table.Column<int>(type: "integer", nullable: false),
                    LastSeenAnnouncementId = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FolderSeenStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FolderSeenStates_Folders_FolderId",
                        column: x => x.FolderId,
                        principalTable: "Folders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssignedTaskInstances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    DueAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReturnReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Returns = table.Column<int>(type: "integer", nullable: false),
                    Reminded = table.Column<bool>(type: "boolean", nullable: false),
                    Alerted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignedTaskInstances", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignedTaskInstances_AssignedTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AssignedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AssignedTaskFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: true),
                    InstanceId = table.Column<int>(type: "integer", nullable: true),
                    OriginalName = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    StorageName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    UploadedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AssignedTaskFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AssignedTaskFiles_AssignedTaskInstances_InstanceId",
                        column: x => x.InstanceId,
                        principalTable: "AssignedTaskInstances",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_AssignedTaskFiles_AssignedTasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "AssignedTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Announcements_FolderId",
                table: "Announcements",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskFiles_InstanceId",
                table: "AssignedTaskFiles",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskFiles_TaskId",
                table: "AssignedTaskFiles",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskInstances_DueAt",
                table: "AssignedTaskInstances",
                column: "DueAt");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTaskInstances_TaskId_State",
                table: "AssignedTaskInstances",
                columns: new[] { "TaskId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTasks_AssigneeUserName",
                table: "AssignedTasks",
                column: "AssigneeUserName");

            migrationBuilder.CreateIndex(
                name: "IX_AssignedTasks_AuthorUserName",
                table: "AssignedTasks",
                column: "AuthorUserName");

            migrationBuilder.CreateIndex(
                name: "IX_FolderHeads_FolderId_UserName",
                table: "FolderHeads",
                columns: new[] { "FolderId", "UserName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FolderHeads_UserName",
                table: "FolderHeads",
                column: "UserName");

            migrationBuilder.CreateIndex(
                name: "IX_FolderSeenStates_FolderId",
                table: "FolderSeenStates",
                column: "FolderId");

            migrationBuilder.CreateIndex(
                name: "IX_FolderSeenStates_UserName_FolderId",
                table: "FolderSeenStates",
                columns: new[] { "UserName", "FolderId" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Announcements_Folders_FolderId",
                table: "Announcements",
                column: "FolderId",
                principalTable: "Folders",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Announcements_Folders_FolderId",
                table: "Announcements");

            migrationBuilder.DropTable(
                name: "AssignedTaskFiles");

            migrationBuilder.DropTable(
                name: "FolderHeads");

            migrationBuilder.DropTable(
                name: "FolderSeenStates");

            migrationBuilder.DropTable(
                name: "AssignedTaskInstances");

            migrationBuilder.DropTable(
                name: "AssignedTasks");

            migrationBuilder.DropIndex(
                name: "IX_Announcements_FolderId",
                table: "Announcements");

            migrationBuilder.DropColumn(
                name: "IsHead",
                table: "SeenStates");

            migrationBuilder.DropColumn(
                name: "DepartmentRole",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "IsDepartment",
                table: "Folders");

            migrationBuilder.DropColumn(
                name: "FolderId",
                table: "Announcements");
        }
    }
}
