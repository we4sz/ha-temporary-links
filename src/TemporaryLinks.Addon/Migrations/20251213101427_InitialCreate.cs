using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemporaryLinks.Addon.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TemporaryLinks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Token = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    Actions = table.Column<string>(type: "TEXT", nullable: false),
                    ValidFrom = table.Column<long>(type: "INTEGER", nullable: false),
                    ValidUntil = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxUses = table.Column<int>(type: "INTEGER", nullable: false),
                    UsageCount = table.Column<int>(type: "INTEGER", nullable: false),
                    WebhookId = table.Column<string>(type: "TEXT", nullable: false),
                    CloudhookId = table.Column<string>(type: "TEXT", nullable: false),
                    CloudhookUrl = table.Column<string>(type: "TEXT", nullable: false),
                    RecipientPhoneNumber = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CustomMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TemporaryLinks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LinkSmsAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemporaryLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TwilioMessageSid = table.Column<string>(type: "TEXT", nullable: false),
                    Content = table.Column<string>(type: "TEXT", nullable: false),
                    SmsSent = table.Column<bool>(type: "INTEGER", nullable: false),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkSmsAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkSmsAudits_TemporaryLinks_TemporaryLinkId",
                        column: x => x.TemporaryLinkId,
                        principalTable: "TemporaryLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LinkUsageAudits",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TemporaryLinkId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    IpAddress = table.Column<string>(type: "TEXT", nullable: true),
                    UserAgent = table.Column<string>(type: "TEXT", nullable: true),
                    Success = table.Column<bool>(type: "INTEGER", nullable: false),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    Timestamp = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LinkUsageAudits", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LinkUsageAudits_TemporaryLinks_TemporaryLinkId",
                        column: x => x.TemporaryLinkId,
                        principalTable: "TemporaryLinks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LinkSmsAudits_TemporaryLinkId",
                table: "LinkSmsAudits",
                column: "TemporaryLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkSmsAudits_Timestamp",
                table: "LinkSmsAudits",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_LinkUsageAudits_TemporaryLinkId",
                table: "LinkUsageAudits",
                column: "TemporaryLinkId");

            migrationBuilder.CreateIndex(
                name: "IX_LinkUsageAudits_Timestamp",
                table: "LinkUsageAudits",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryLinks_Status",
                table: "TemporaryLinks",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryLinks_Token",
                table: "TemporaryLinks",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TemporaryLinks_ValidUntil",
                table: "TemporaryLinks",
                column: "ValidUntil");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LinkSmsAudits");

            migrationBuilder.DropTable(
                name: "LinkUsageAudits");

            migrationBuilder.DropTable(
                name: "TemporaryLinks");
        }
    }
}
