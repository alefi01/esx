using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ПраваНаЧеловека : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsUser",
                table: "FolderPermissions",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsUser",
                table: "FolderPermissions");
        }
    }
}
