using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace MediaDownloader.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ProviderCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProviderName = table.Column<string>(type: "TEXT", nullable: false),
                    Username = table.Column<string>(type: "TEXT", nullable: false),
                    Password = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SeriesTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Query = table.Column<string>(type: "TEXT", nullable: false),
                    Provider = table.Column<string>(type: "TEXT", nullable: true),
                    TitleFilter = table.Column<string>(type: "TEXT", nullable: true),
                    Season = table.Column<int>(type: "INTEGER", nullable: true),
                    StartEpisode = table.Column<int>(type: "INTEGER", nullable: false),
                    EndEpisode = table.Column<int>(type: "INTEGER", nullable: true),
                    DownloadFolder = table.Column<string>(type: "TEXT", nullable: true),
                    LastDownloadedEpisode = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckIntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    LastCheckedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SeriesTasks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadFolder = table.Column<string>(type: "TEXT", nullable: false),
                    PostDownloadAction = table.Column<int>(type: "INTEGER", nullable: false),
                    DisabledProviders = table.Column<string>(type: "TEXT", nullable: false),
                    Language = table.Column<string>(type: "TEXT", nullable: false),
                    NotifyOnStart = table.Column<bool>(type: "INTEGER", nullable: false),
                    NotifyOnComplete = table.Column<bool>(type: "INTEGER", nullable: false),
                    EmailEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmtpHost = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPort = table.Column<int>(type: "INTEGER", nullable: false),
                    SmtpUseSsl = table.Column<bool>(type: "INTEGER", nullable: false),
                    SmtpUsername = table.Column<string>(type: "TEXT", nullable: false),
                    SmtpPassword = table.Column<string>(type: "TEXT", nullable: false),
                    EmailFrom = table.Column<string>(type: "TEXT", nullable: false),
                    EmailTo = table.Column<string>(type: "TEXT", nullable: false),
                    DesktopEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    PushEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    NtfyServer = table.Column<string>(type: "TEXT", nullable: false),
                    NtfyTopic = table.Column<string>(type: "TEXT", nullable: false),
                    TelegramEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramBotToken = table.Column<string>(type: "TEXT", nullable: false),
                    TelegramChatId = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Downloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    MagnetUri = table.Column<string>(type: "TEXT", nullable: false),
                    TorrentFilePath = table.Column<string>(type: "TEXT", nullable: true),
                    InfoHash = table.Column<string>(type: "TEXT", nullable: false),
                    SavePath = table.Column<string>(type: "TEXT", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    NameIsPlaceholder = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    Progress = table.Column<double>(type: "REAL", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    StartNotificationSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompleteNotificationSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    SeriesTaskId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Downloads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Downloads_SeriesTasks_SeriesTaskId",
                        column: x => x.SeriesTaskId,
                        principalTable: "SeriesTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_InfoHash",
                table: "Downloads",
                column: "InfoHash",
                unique: true,
                filter: "\"InfoHash\" <> ''");

            migrationBuilder.CreateIndex(
                name: "IX_Downloads_SeriesTaskId",
                table: "Downloads",
                column: "SeriesTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderCredentials_ProviderName",
                table: "ProviderCredentials",
                column: "ProviderName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Downloads");

            migrationBuilder.DropTable(
                name: "ProviderCredentials");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "SeriesTasks");
        }
    }
}
