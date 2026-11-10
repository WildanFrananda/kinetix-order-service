using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersSearchNewestFirstForStaff : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateIndex(
                name: "ix_orders_merchant_newest_first",
                table: "orders",
                columns: new[] { "merchant_principal_id", "created_at", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_orders_newest_first",
                table: "orders",
                columns: new[] { "created_at", "id" }
            );

            migrationBuilder.CreateIndex(
                name: "ix_orders_status_newest_first",
                table: "orders",
                columns: new[] { "status", "created_at", "id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "ix_orders_merchant_newest_first",
                table: "orders"
            );

            migrationBuilder.DropIndex(
                name: "ix_orders_newest_first",
                table: "orders"
            );

            migrationBuilder.DropIndex(
                name: "ix_orders_status_newest_first",
                table: "orders"
            );
        }
    }
}
