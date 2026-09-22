using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S13OrderRemembersItsMerchant : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "merchant_principal_id",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.Sql(@"
                UPDATE orders o
                SET merchant_principal_id = seller.merchant_principal_id
                FROM (
                    SELECT DISTINCT ON (saga.order_number)
                           saga.order_number,
                           step.merchant_principal_id
                    FROM checkout_sagas saga
                    JOIN checkout_saga_steps step ON step.saga_id = saga.id
                    WHERE step.merchant_principal_id <> ''
                    ORDER BY saga.order_number, step.created_at
                ) AS seller
                WHERE o.order_number = seller.order_number
                  AND o.merchant_principal_id = ''
            ");

            migrationBuilder.CreateIndex(
                name: "ix_orders_changed_since",
                table: "orders",
                columns: new[] { "updated_at", "order_number" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "ix_orders_changed_since",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "merchant_principal_id",
                table: "orders");
        }
    }
}
