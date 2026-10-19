using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersKnowTheirDropOffAndParcelWeight : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<double>(
                name: "delivery_latitude",
                table: "orders",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "delivery_longitude",
                table: "orders",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "parcel_weight_grams",
                table: "orders",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropColumn(
                name: "delivery_latitude",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivery_longitude",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "parcel_weight_grams",
                table: "orders");
        }
    }
}
