using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersRecordTheirChangeTransaction : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.AddColumn<ulong>(
                name: "change_transaction_id",
                table: "orders",
                type: "xid8",
                nullable: false,
                defaultValueSql: "pg_current_xact_id()");

            migrationBuilder.CreateIndex(
                name: "ix_orders_change_feed",
                table: "orders",
                columns: new[] { "change_transaction_id", "order_number" });

            migrationBuilder.Sql(@"
CREATE FUNCTION orders_stamp_change_transaction() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    NEW.change_transaction_id := pg_current_xact_id();
    RETURN NEW;
END
$$;

CREATE TRIGGER orders_stamp_change_transaction
BEFORE INSERT OR UPDATE ON orders
FOR EACH ROW EXECUTE FUNCTION orders_stamp_change_transaction();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.Sql(@"
DROP TRIGGER orders_stamp_change_transaction ON orders;
DROP FUNCTION orders_stamp_change_transaction();
");

            migrationBuilder.DropIndex(
                name: "ix_orders_change_feed",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "change_transaction_id",
                table: "orders");
        }
    }
}
