using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S11CheckoutSaga : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "checkout_sagas",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<string>(type: "text", nullable: false),
                    customer_principal_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_reason = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_checkout_sagas", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "checkout_saga_steps",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    saga_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    reference = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    merchant_principal_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    detail = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_checkout_saga_steps", x => x.id);
                    table.ForeignKey(
                        name: "FK_checkout_saga_steps_checkout_sagas_saga_id",
                        column: x => x.saga_id,
                        principalTable: "checkout_sagas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_saga_steps_saga_id",
                table: "checkout_saga_steps",
                column: "saga_id");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sagas_order_number",
                table: "checkout_sagas",
                column: "order_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sagas_state_updated_at",
                table: "checkout_sagas",
                columns: new[] { "state", "updated_at" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "checkout_saga_steps");

            migrationBuilder.DropTable(
                name: "checkout_sagas");
        }
    }
}
