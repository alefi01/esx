using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Portal.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class ЗакреплённоеСообщение : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PinnedMessageId",
                table: "Conversations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PinnedMessageId",
                table: "Conversations");
        }
    }
}
