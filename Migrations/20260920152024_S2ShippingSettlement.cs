using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S2ShippingSettlement : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "shipping_settlements",
                columns: table => new {
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    driver_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    delivered_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    settled_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_shipping_settlements", x => x.order_number);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shipping_settlements_settled_at_next_attempt_at",
                table: "shipping_settlements",
                columns: new[] { "settled_at", "next_attempt_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "shipping_settlements");
        }
    }
}
