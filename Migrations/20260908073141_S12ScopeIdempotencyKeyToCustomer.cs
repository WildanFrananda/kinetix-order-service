using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S12ScopeIdempotencyKeyToCustomer : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_orders_idempotency_key",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_principal_id_idempotency_key",
                table: "orders",
                columns: new[] { "customer_principal_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_orders_customer_principal_id_idempotency_key",
                table: "orders");

            migrationBuilder.CreateIndex(
                name: "IX_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);
        }
    }
}
