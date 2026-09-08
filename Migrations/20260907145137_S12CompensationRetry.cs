using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Kinetix.OrderService.Migrations {
    /// <inheritdoc />
    public partial class S12CompensationRetry : Migration {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropIndex(
                name: "IX_checkout_sagas_state_updated_at",
                table: "checkout_sagas");

            migrationBuilder.AddColumn<DateTime>(
                name: "abandoned_at",
                table: "checkout_sagas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                table: "checkout_sagas",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                table: "checkout_sagas",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "lease_expires_at",
                table: "checkout_sagas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "lease_owner",
                table: "checkout_sagas",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "needs_attention_at",
                table: "checkout_sagas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at",
                table: "checkout_sagas",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "compensated_by_repeat",
                table: "checkout_saga_steps",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "compensation_attempts",
                table: "checkout_saga_steps",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "last_failure_code",
                table: "checkout_saga_steps",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "checkout_saga_compensation_attempts",
                columns: table => new {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    saga_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attempt_no = table.Column<int>(type: "integer", nullable: false),
                    worker = table.Column<string>(type: "text", nullable: false),
                    started_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    finished_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    detail = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table => {
                    table.PrimaryKey("PK_checkout_saga_compensation_attempts", x => x.id);
                    table.ForeignKey(
                        name: "FK_checkout_saga_compensation_attempts_checkout_sagas_saga_id",
                        column: x => x.saga_id,
                        principalTable: "checkout_sagas",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sagas_attention",
                table: "checkout_sagas",
                column: "needs_attention_at",
                filter: "needs_attention_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_checkout_sagas_due",
                table: "checkout_sagas",
                columns: new[] { "next_attempt_at", "updated_at" },
                filter: "state IN ('Running', 'Compensating', 'Stuck')");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_saga_compensation_attempts_saga_id_attempt_no",
                table: "checkout_saga_compensation_attempts",
                columns: new[] { "saga_id", "attempt_no" },
                unique: true);

            migrationBuilder.Sql(@"
                UPDATE checkout_saga_steps st
                   SET compensation_attempts = 1
                  FROM checkout_sagas s
                 WHERE s.id = st.saga_id
                   AND s.state IN ('Compensating', 'Stuck')
                   AND st.name = 'CreateEscrowHold'
                   AND st.state <> 'Compensated';");

            migrationBuilder.Sql(@"
                UPDATE checkout_sagas s
                   SET state = 'Abandoned',
                       abandoned_at = COALESCE(s.abandoned_at, now()),
                       needs_attention_at = COALESCE(s.needs_attention_at, now()),
                       last_failure_code = COALESCE(s.last_failure_code, 'ESCROW_REFUND_UNCONFIRMED'),
                       next_attempt_at = NULL,
                       updated_at = now()
                 WHERE s.state IN ('Compensating', 'Stuck')
                   AND EXISTS (SELECT 1
                                 FROM checkout_saga_steps st
                                WHERE st.saga_id = s.id
                                  AND st.name = 'CreateEscrowHold'
                                  AND st.state <> 'Compensated');");

            migrationBuilder.Sql("UPDATE checkout_sagas SET next_attempt_at = now() WHERE state = 'Stuck';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) {
            migrationBuilder.DropTable(
                name: "checkout_saga_compensation_attempts");

            migrationBuilder.DropIndex(
                name: "ix_checkout_sagas_attention",
                table: "checkout_sagas");

            migrationBuilder.DropIndex(
                name: "ix_checkout_sagas_due",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "abandoned_at",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "last_failure_code",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "lease_expires_at",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "lease_owner",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "needs_attention_at",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "next_attempt_at",
                table: "checkout_sagas");

            migrationBuilder.DropColumn(
                name: "compensated_by_repeat",
                table: "checkout_saga_steps");

            migrationBuilder.DropColumn(
                name: "compensation_attempts",
                table: "checkout_saga_steps");

            migrationBuilder.DropColumn(
                name: "last_failure_code",
                table: "checkout_saga_steps");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sagas_state_updated_at",
                table: "checkout_sagas",
                columns: new[] { "state", "updated_at" });
        }
    }
}
