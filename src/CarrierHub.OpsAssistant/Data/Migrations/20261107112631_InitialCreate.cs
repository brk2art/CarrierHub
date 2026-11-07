using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace CarrierHub.OpsAssistant.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AuditEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Event = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PendingActions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MerchantId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConversationId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ShipmentId = table.Column<Guid>(type: "uuid", nullable: false),
                    MerchantReference = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Carrier = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProposedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PendingActions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEntries_ActionId",
                table: "AuditEntries",
                column: "ActionId");

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_IdempotencyKey",
                table: "PendingActions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_MerchantId_ProposedAt",
                table: "PendingActions",
                columns: new[] { "MerchantId", "ProposedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PendingActions_MerchantId_ShipmentId_Reason",
                table: "PendingActions",
                columns: new[] { "MerchantId", "ShipmentId", "Reason" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuditEntries");

            migrationBuilder.DropTable(
                name: "PendingActions");
        }
    }
}
