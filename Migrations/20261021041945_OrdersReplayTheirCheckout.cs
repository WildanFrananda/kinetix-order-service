using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersReplayTheirCheckout : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "idempotency_fingerprint",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true
            );

            migrationBuilder.CreateIndex(
                name: "ix_orders_pending_payment",
                table: "orders",
                column: "created_at",
                filter: "status = 'PENDING_PAYMENT'"
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "ix_orders_pending_payment",
                table: "orders"
            );

            migrationBuilder.DropColumn(
                name: "idempotency_fingerprint",
                table: "orders"
            );
        }
    }
}
