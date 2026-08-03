using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaDownloader.Data.Migrations
{
    /// <inheritdoc />
    public partial class AgentApiSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AgentApiAllowRemote",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AgentApiEnabled",
                table: "Settings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AgentApiToken",
                table: "Settings",
                type: "TEXT",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentApiAllowRemote",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AgentApiEnabled",
                table: "Settings");

            migrationBuilder.DropColumn(
                name: "AgentApiToken",
                table: "Settings");
        }
    }
}
