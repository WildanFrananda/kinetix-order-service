using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersCloseTheirReturnWindow : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "delivered_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "last_refund_error",
                table: "order_returns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_refund_attempt_at",
                table: "order_returns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "refund_amount",
                table: "order_returns",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "refund_attempts",
                table: "order_returns",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "rejected_at",
                table: "order_returns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rejection_reason",
                table: "order_returns",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "escrow_releases",
                columns: table => new {
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    released_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_escrow_releases", x => x.order_number);
                });

            migrationBuilder.Sql(
                "UPDATE orders o SET delivered_at = s.delivered_at "
              + "FROM shipping_settlements s "
              + "WHERE s.order_number = o.order_number AND o.status IN ('DELIVERED', 'COMPLETED');"
            );

            migrationBuilder.CreateIndex(
                name: "ix_orders_awaiting_completion",
                table: "orders",
                column: "delivered_at",
                filter: "status = 'DELIVERED'");

            migrationBuilder.CreateIndex(
                name: "ix_order_returns_refund_due",
                table: "order_returns",
                column: "next_refund_attempt_at",
                filter: "status = 'GOODS_RECEIVED'");

            migrationBuilder.CreateIndex(
                name: "ix_escrow_releases_due",
                table: "escrow_releases",
                column: "next_attempt_at",
                filter: "released_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "escrow_releases");

            migrationBuilder.DropIndex(
                name: "ix_orders_awaiting_completion",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "ix_order_returns_refund_due",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "completed_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "delivered_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "last_refund_error",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "next_refund_attempt_at",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "refund_amount",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "refund_attempts",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "rejected_at",
                table: "order_returns");

            migrationBuilder.DropColumn(
                name: "rejection_reason",
                table: "order_returns");
        }
    }
}
