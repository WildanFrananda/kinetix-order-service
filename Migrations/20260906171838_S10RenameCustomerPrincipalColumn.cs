using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S10RenameCustomerPrincipalColumn : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_orders_customer_id",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "orders");

            migrationBuilder.RenameColumn(
                name: "CustomerPrincipalId",
                table: "orders",
                newName: "customer_principal_id");

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_principal_id",
                table: "orders",
                column: "customer_principal_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_orders_customer_principal_id",
                table: "orders");

            migrationBuilder.RenameColumn(
                name: "customer_principal_id",
                table: "orders",
                newName: "CustomerPrincipalId");

            migrationBuilder.AddColumn<long>(
                name: "customer_id",
                table: "orders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_orders_customer_id",
                table: "orders",
                column: "customer_id");
        }
    }
}
