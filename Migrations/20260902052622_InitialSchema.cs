using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class InitialSchema : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    customer_id = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    applied_voucher = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    final_total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    shipping_service_tier = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    base_shipping_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    shipping_discount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    final_shipping_fee = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    distance_km = table.Column<double>(type: "double precision", nullable: false),
                    shipping_address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_orders", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    product_title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_order_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_items_order_id",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_id",
                table: "orders",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_order_number",
                table: "orders",
                column: "order_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
