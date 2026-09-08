using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S12ShippingQuoteBasis : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "shipping_quote_basis",
                table: "orders",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "CLIENT_SUPPLIED");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropColumn(
                name: "shipping_quote_basis",
                table: "orders");
        }
    }
}
