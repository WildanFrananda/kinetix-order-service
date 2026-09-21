using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class W10OrderOwnsReturns : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "order_returns",
                columns: table => new {
                    return_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    order_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    merchant_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    opened_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    goods_received_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    bin_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    resolved_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_order_returns", x => x.return_number);
                });

            migrationBuilder.CreateTable(
                name: "order_return_lines",
                columns: table => new {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    return_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    sku = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_order_return_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_return_lines_order_returns_return_number",
                        column: x => x.return_number,
                        principalTable: "order_returns",
                        principalColumn: "return_number",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_return_lines_return_number",
                table: "order_return_lines",
                column: "return_number");

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_merchant_principal_id_status",
                table: "order_returns",
                columns: new[] { "merchant_principal_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_order_returns_order_number",
                table: "order_returns",
                column: "order_number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "order_return_lines");

            migrationBuilder.DropTable(
                name: "order_returns");
        }
    }
}
