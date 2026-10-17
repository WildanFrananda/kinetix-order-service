using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class OrdersKeepALifecycleLog : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.CreateTable(
                name: "order_lifecycle_events",
                columns: table => new {
                    sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    transaction_id = table.Column<ulong>(type: "xid8", nullable: false, defaultValueSql: "pg_current_xact_id()"),
                    order_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    from_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    to_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    return_number = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    return_status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    actor_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    actor_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    buyer_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    merchant_principal_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    occurred_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table => {
                    table.PrimaryKey("PK_order_lifecycle_events", x => x.sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_order_lifecycle_events_order_number",
                table: "order_lifecycle_events",
                column: "order_number");

            migrationBuilder.CreateIndex(
                name: "ix_order_lifecycle_events_position",
                table: "order_lifecycle_events",
                columns: new[] { "transaction_id", "sequence" });

            migrationBuilder.Sql(@"
INSERT INTO order_lifecycle_events (
    order_number, kind, to_status, actor_kind, buyer_principal_id, merchant_principal_id, occurred_at
)
SELECT o.order_number, 'BASELINE', o.status, 'SYSTEM', o.customer_principal_id, o.merchant_principal_id, now()
FROM orders o
ORDER BY o.created_at, o.order_number;

INSERT INTO order_lifecycle_events (
    order_number, kind, return_number, return_status, actor_kind, buyer_principal_id, merchant_principal_id,
    occurred_at
)
SELECT r.order_number, 'BASELINE', r.return_number, r.status, 'SYSTEM', o.customer_principal_id,
       r.merchant_principal_id, now()
FROM order_returns r
JOIN orders o ON o.order_number = r.order_number
ORDER BY r.opened_at, r.return_number;

CREATE FUNCTION order_lifecycle_events_are_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'order_lifecycle_events is append-only: % refused', TG_OP;
END
$$;

CREATE TRIGGER order_lifecycle_events_no_update_or_delete
    BEFORE UPDATE OR DELETE ON order_lifecycle_events
    FOR EACH ROW EXECUTE FUNCTION order_lifecycle_events_are_append_only();

CREATE TRIGGER order_lifecycle_events_no_truncate
    BEFORE TRUNCATE ON order_lifecycle_events
    FOR EACH STATEMENT EXECUTE FUNCTION order_lifecycle_events_are_append_only();

CREATE FUNCTION order_status_is_logged() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM order_lifecycle_events e
        WHERE e.order_number = NEW.order_number
          AND e.kind = 'STATUS_CHANGED'
          AND e.to_status = NEW.status
          AND e.transaction_id = pg_current_xact_id()
    ) THEN
        RAISE EXCEPTION 'order % reached % with no lifecycle event in the same transaction',
            NEW.order_number, NEW.status;
    END IF;
    RETURN NULL;
END
$$;

CREATE CONSTRAINT TRIGGER orders_insert_is_logged
    AFTER INSERT ON orders
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION order_status_is_logged();

CREATE CONSTRAINT TRIGGER orders_status_change_is_logged
    AFTER UPDATE OF status ON orders
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION order_status_is_logged();

CREATE FUNCTION order_return_status_is_logged() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM order_lifecycle_events e
        WHERE e.return_number = NEW.return_number
          AND e.kind IN ('RETURN_OPENED', 'RETURN_GOODS_RECEIVED', 'RETURN_RESOLVED', 'RETURN_REJECTED')
          AND e.return_status = NEW.status
          AND e.transaction_id = pg_current_xact_id()
    ) THEN
        RAISE EXCEPTION 'return % reached % with no lifecycle event in the same transaction',
            NEW.return_number, NEW.status;
    END IF;
    RETURN NULL;
END
$$;

CREATE CONSTRAINT TRIGGER order_returns_insert_is_logged
    AFTER INSERT ON order_returns
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION order_return_status_is_logged();

CREATE CONSTRAINT TRIGGER order_returns_status_change_is_logged
    AFTER UPDATE OF status ON order_returns
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW WHEN (OLD.status IS DISTINCT FROM NEW.status)
    EXECUTE FUNCTION order_return_status_is_logged();
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.Sql(@"
DROP TRIGGER order_returns_status_change_is_logged ON order_returns;
DROP TRIGGER order_returns_insert_is_logged ON order_returns;
DROP FUNCTION order_return_status_is_logged();
DROP TRIGGER orders_status_change_is_logged ON orders;
DROP TRIGGER orders_insert_is_logged ON orders;
DROP FUNCTION order_status_is_logged();
DROP TRIGGER order_lifecycle_events_no_truncate ON order_lifecycle_events;
DROP TRIGGER order_lifecycle_events_no_update_or_delete ON order_lifecycle_events;
DROP FUNCTION order_lifecycle_events_are_append_only();
");

            migrationBuilder.DropTable(
                name: "order_lifecycle_events");
        }
    }
}
