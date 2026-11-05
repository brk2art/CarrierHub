using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CarrierHub.Tracking.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClaims : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Claims",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Carrier = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Claims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Claims_Shipments_ShipmentId",
                        column: x => x.ShipmentId,
                        principalTable: "Shipments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_MerchantId_CreatedAt",
                table: "Claims",
                columns: new[] { "MerchantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Claims_MerchantId_IdempotencyKey",
                table: "Claims",
                columns: new[] { "MerchantId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Claims_ShipmentId_Reason",
                table: "Claims",
                columns: new[] { "ShipmentId", "Reason" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Claims");
        }
    }
}
