using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KartOrderService.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_audit_log",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_name = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    actor_type = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<string>(type: "text", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    metadata = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_audit_log", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    total_amount = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    payment_intent_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tracking_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.order_id);
                });

            migrationBuilder.CreateTable(
                name: "order_events",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    from_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    to_status = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    event_type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    payload = table.Column<string>(type: "jsonb", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    projected_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_events_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "order_items",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    qty = table.Column<int>(type: "integer", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(12,2)", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    reservation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    reservation_confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    updated_by = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_order_items_orders_order_id",
                        column: x => x.order_id,
                        principalTable: "orders",
                        principalColumn: "order_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "idx_order_audit_log_entity",
                table: "order_audit_log",
                columns: new[] { "entity_type", "entity_id" });

            migrationBuilder.CreateIndex(
                name: "idx_order_events_order_seq",
                table: "order_events",
                columns: new[] { "order_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_outbox_unpublished",
                table: "order_events",
                column: "created_at",
                filter: "event_type IS NOT NULL AND published_at IS NULL");

            migrationBuilder.CreateIndex(
                name: "idx_order_items_order_id",
                table: "order_items",
                column: "order_id");

            migrationBuilder.CreateIndex(
                name: "idx_orders_idempotency_key",
                table: "orders",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "idx_orders_status_created",
                table: "orders",
                columns: new[] { "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "idx_orders_tracking_id",
                table: "orders",
                column: "tracking_id");

            migrationBuilder.CreateIndex(
                name: "idx_orders_user_id",
                table: "orders",
                column: "user_id");

            // database-design.md's Row-Level Security Policy section, verbatim shape (session-scoped
            // `app.current_principal[_kind]`, set by EfUnitOfWork.BeginPrincipalScopedTransactionAsync
            // via set_config before any query). `current_setting(..., true)` (missing_ok=true) is used
            // rather than the doc's literal `current_setting(...)` so a session that never set the
            // GUC (migrations, ad-hoc psql) gets NULL - which fails every comparison below closed
            // (no access), rather than raising an error.
            //
            // FORCE ROW LEVEL SECURITY is added because Postgres table owners (and superusers,
            // unconditionally) bypass RLS by default - this local dev docker-compose's `postgres`
            // connection is the table owner AND a superuser, so RLS has no observable effect there
            // regardless of FORCE; verifying these policies actually restrict access requires
            // connecting as a distinct, non-owner, non-superuser application role (README's
            // verification section notes this explicitly).
            migrationBuilder.Sql(
                """
                ALTER TABLE orders ENABLE ROW LEVEL SECURITY;
                ALTER TABLE orders FORCE ROW LEVEL SECURITY;
                CREATE POLICY orders_owner_isolation ON orders
                    USING (
                        current_setting('app.current_principal_kind', true) IN ('service', 'system')
                        OR user_id = current_setting('app.current_principal', true)::uuid
                    );

                ALTER TABLE order_items ENABLE ROW LEVEL SECURITY;
                ALTER TABLE order_items FORCE ROW LEVEL SECURITY;
                CREATE POLICY order_items_owner_isolation ON order_items
                    USING (
                        current_setting('app.current_principal_kind', true) IN ('service', 'system')
                        OR EXISTS (
                            SELECT 1 FROM orders o
                            WHERE o.order_id = order_items.order_id
                              AND o.user_id = current_setting('app.current_principal', true)::uuid
                        )
                    );

                ALTER TABLE order_events ENABLE ROW LEVEL SECURITY;
                ALTER TABLE order_events FORCE ROW LEVEL SECURITY;
                CREATE POLICY order_events_owner_isolation ON order_events
                    USING (
                        current_setting('app.current_principal_kind', true) IN ('service', 'system')
                        OR EXISTS (
                            SELECT 1 FROM orders o
                            WHERE o.order_id = order_events.order_id
                              AND o.user_id = current_setting('app.current_principal', true)::uuid
                        )
                    );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP POLICY IF EXISTS order_events_owner_isolation ON order_events;
                DROP POLICY IF EXISTS order_items_owner_isolation ON order_items;
                DROP POLICY IF EXISTS orders_owner_isolation ON orders;
                """);

            migrationBuilder.DropTable(
                name: "order_audit_log");

            migrationBuilder.DropTable(
                name: "order_events");

            migrationBuilder.DropTable(
                name: "order_items");

            migrationBuilder.DropTable(
                name: "orders");
        }
    }
}
