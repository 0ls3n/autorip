using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AutoRip.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RipJobs",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    DiscLabel = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    MovieName = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    OutputDir = table.Column<string>(type: "TEXT", maxLength: 512, nullable: false),
                    MkvPath = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    Mp4Path = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    SubtitlesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RipProgress = table.Column<double>(type: "REAL", nullable: false),
                    ProcessingProgress = table.Column<double>(type: "REAL", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DeleteMkvAfterTranscode = table.Column<bool>(type: "INTEGER", nullable: false),
                    HandbrakePreset = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TransferMode = table.Column<int>(type: "INTEGER", nullable: false),
                    MovieInfoJson = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RipJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "RipLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RipJobId = table.Column<string>(type: "TEXT", maxLength: 36, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Message = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RipLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RipLogs_RipJobs_RipJobId",
                        column: x => x.RipJobId,
                        principalTable: "RipJobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RipJobs_CreatedAt",
                table: "RipJobs",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_RipJobs_Status",
                table: "RipJobs",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_RipLogs_RipJobId",
                table: "RipLogs",
                column: "RipJobId");

            migrationBuilder.CreateIndex(
                name: "IX_RipLogs_Timestamp",
                table: "RipLogs",
                column: "Timestamp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RipLogs");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "RipJobs");
        }
    }
}
