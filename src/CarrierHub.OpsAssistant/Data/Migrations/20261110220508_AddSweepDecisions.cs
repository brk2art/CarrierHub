using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarrierHub.OpsAssistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSweepDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SweepDecisions",
                columns: table => new
                {
                    MerchantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SweepId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SweepDecisions", x => new { x.MerchantId, x.ShipmentId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_SweepDecisions_MerchantId_Outcome",
                table: "SweepDecisions",
                columns: new[] { "MerchantId", "Outcome" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SweepDecisions");
        }
    }
}
