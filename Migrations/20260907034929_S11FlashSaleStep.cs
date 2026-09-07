using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S11FlashSaleStep : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<string>(
                name: "product_id",
                table: "checkout_saga_steps",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropColumn(
                name: "product_id",
                table: "checkout_saga_steps");
        }
    }
}
