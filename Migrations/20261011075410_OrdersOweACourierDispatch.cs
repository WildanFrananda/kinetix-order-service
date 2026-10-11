using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersOweACourierDispatch : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "courier_dispatches",
                columns: table => new {
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    merchant_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fulfillment_task_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    dispatch_ref = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    awb_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    dispatched_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    awb_recorded_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    last_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    next_attempt_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_courier_dispatches", x => x.order_number);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "courier_dispatches");
        }
    }
}
