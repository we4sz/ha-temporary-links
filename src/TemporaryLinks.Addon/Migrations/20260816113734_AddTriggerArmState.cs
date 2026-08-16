using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TemporaryLinks.Addon.Migrations
{
    /// <inheritdoc />
    public partial class AddTriggerArmState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "LastTriggerProcessedAt",
                table: "TemporaryLinks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TriggerAcceptsPost",
                table: "TemporaryLinks",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastTriggerProcessedAt",
                table: "TemporaryLinks");

            migrationBuilder.DropColumn(
                name: "TriggerAcceptsPost",
                table: "TemporaryLinks");
        }
    }
}
