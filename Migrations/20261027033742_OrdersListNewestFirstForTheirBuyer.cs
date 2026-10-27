using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersListNewestFirstForTheirBuyer : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_orders_customer_principal_id",
                table: "orders"
            );

            migrationBuilder.CreateIndex(
                name: "ix_orders_customer_newest_first",
                table: "orders",
                columns: new[] { "customer_principal_id", "created_at", "id" }
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "ix_orders_customer_newest_first",
                table: "orders"
            );

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_principal_id",
                table: "orders",
                column: "customer_principal_id"
            );
        }
    }
}
