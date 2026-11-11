using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Juice.Notifications.EF.PostgreSQL.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "notifications");

            migrationBuilder.CreateTable(
                name: "applications",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    client_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_applications", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "destination_allowlist",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ports = table.Column<int[]>(type: "integer[]", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_destination_allowlist", x => new { x.tenant_id, x.destination });
                });

            migrationBuilder.CreateTable(
                name: "integrations",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    settings = table.Column<string>(type: "jsonb", nullable: false),
                    secret_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    priority = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integrations", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "outbox_events",
                schema: "notifications",
                columns: table => new
                {
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_type_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    creation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    payload_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    headers = table.Column<string>(type: "text", nullable: false, defaultValue: "{}"),
                    transaction_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    tenant_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_events", x => x.event_id);
                });

            migrationBuilder.CreateTable(
                name: "routes",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    match = table.Column<string>(type: "jsonb", nullable: false),
                    targets = table.Column<string[]>(type: "text[]", nullable: false),
                    digest_seconds = table.Column<int>(type: "integer", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_routes", x => new { x.tenant_id, x.id });
                });

            migrationBuilder.CreateTable(
                name: "application_tenants",
                schema: "notifications",
                columns: table => new
                {
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_application_tenants", x => new { x.application_id, x.tenant_id });
                    table.ForeignKey(
                        name: "fk_application_tenants_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "notifications",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_types",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    category = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    display_group = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    external_recipients = table.Column<bool>(type: "boolean", nullable: false),
                    schema = table.Column<string>(type: "jsonb", nullable: false),
                    schema_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    workflow = table.Column<string>(type: "jsonb", nullable: false),
                    redact = table.Column<string[]>(type: "text[]", nullable: false),
                    actions = table.Column<string>(type: "jsonb", nullable: true),
                    declaration_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_types", x => x.id);
                    table.ForeignKey(
                        name: "fk_notification_types_applications_application_id",
                        column: x => x.application_id,
                        principalSchema: "notifications",
                        principalTable: "applications",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "deliveries",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_key = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    destination_snapshot = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    template_version = table.Column<int>(type: "integer", nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    renderer_version = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    mandatory = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    failure_category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    provider_message_id = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    first_attempt_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_deliveries", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_deliveries_integrations_tenant_id_integration_id",
                        columns: x => new { x.tenant_id, x.integration_id },
                        principalSchema: "notifications",
                        principalTable: "integrations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "integration_targets",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    address = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    address_ref = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    managed_by = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    definition_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_integration_targets", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_integration_targets_integrations_tenant_id_integration_id",
                        columns: x => new { x.tenant_id, x.integration_id },
                        principalSchema: "notifications",
                        principalTable: "integrations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "outbox_deliveries",
                schema: "notifications",
                columns: table => new
                {
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    publisher_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    creation_time = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    state = table.Column<int>(type: "integer", nullable: false),
                    retry_count = table.Column<int>(type: "integer", nullable: false),
                    processed_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    next_attempt_on = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    processed_by = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_outbox_deliveries", x => x.delivery_id);
                    table.ForeignKey(
                        name: "fk_outbox_deliveries_outbox_events_event_id",
                        column: x => x.event_id,
                        principalSchema: "notifications",
                        principalTable: "outbox_events",
                        principalColumn: "event_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "requests",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    application_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    type_version = table.Column<int>(type: "integer", nullable: false),
                    schema_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    correlation_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    priority = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    labels = table.Column<string>(type: "jsonb", nullable: false),
                    data = table.Column<string>(type: "jsonb", nullable: false),
                    recipients = table.Column<string>(type: "jsonb", nullable: false),
                    actor = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    links = table.Column<string>(type: "jsonb", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    accepted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    trace_parent = table.Column<string>(type: "character varying(55)", maxLength: 55, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_requests", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_requests_application_tenants_application_id_tenant_id",
                        columns: x => new { x.application_id, x.tenant_id },
                        principalSchema: "notifications",
                        principalTable: "application_tenants",
                        principalColumns: new[] { "application_id", "tenant_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_requests_notification_types_type_id",
                        column: x => x.type_id,
                        principalSchema: "notifications",
                        principalTable: "notification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "templates",
                schema: "notifications",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: true),
                    type_id = table.Column<Guid>(type: "uuid", nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    locale = table.Column<string>(type: "character varying(35)", maxLength: 35, nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    engine = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "text", nullable: true),
                    body = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_templates", x => x.id);
                    table.ForeignKey(
                        name: "fk_templates_notification_types_type_id",
                        column: x => x.type_id,
                        principalSchema: "notifications",
                        principalTable: "notification_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "delivery_attempts",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    number = table.Column<int>(type: "integer", nullable: false),
                    integration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    outcome = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    category = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    status_code = table.Column<int>(type: "integer", nullable: true),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    duration_ms = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_attempts", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_delivery_attempts_deliveries_tenant_id_delivery_id",
                        columns: x => new { x.tenant_id, x.delivery_id },
                        principalSchema: "notifications",
                        principalTable: "deliveries",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_delivery_attempts_integrations_tenant_id_integration_id",
                        columns: x => new { x.tenant_id, x.integration_id },
                        principalSchema: "notifications",
                        principalTable: "integrations",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    request_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    workflow_step = table.Column<int>(type: "integer", nullable: false),
                    error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notifications", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_notifications_requests_tenant_id_request_id",
                        columns: x => new { x.tenant_id, x.request_id },
                        principalSchema: "notifications",
                        principalTable: "requests",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_recipients",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    notification_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    route_id = table.Column<Guid>(type: "uuid", nullable: true),
                    route_key = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    address = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    resolver = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: false),
                    named_as = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    escalation_step = table.Column<int>(type: "integer", nullable: false),
                    outcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    claimed_by = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    next_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_notification_recipients", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_notification_recipients_notifications_tenant_id_notificatio",
                        columns: x => new { x.tenant_id, x.notification_id },
                        principalSchema: "notifications",
                        principalTable: "notifications",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "delivery_items",
                schema: "notifications",
                columns: table => new
                {
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    delivery_id = table.Column<Guid>(type: "uuid", nullable: false),
                    recipient_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination_key = table.Column<string>(type: "character varying(2100)", maxLength: 2100, nullable: false),
                    channel = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_delivery_items", x => new { x.tenant_id, x.id });
                    table.ForeignKey(
                        name: "fk_delivery_items_deliveries_tenant_id_delivery_id",
                        columns: x => new { x.tenant_id, x.delivery_id },
                        principalSchema: "notifications",
                        principalTable: "deliveries",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_delivery_items_notification_recipients_tenant_id_recipient_",
                        columns: x => new { x.tenant_id, x.recipient_id },
                        principalSchema: "notifications",
                        principalTable: "notification_recipients",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_application_tenants_tenant_id",
                schema: "notifications",
                table: "application_tenants",
                column: "tenant_id");

            migrationBuilder.CreateIndex(
                name: "ix_applications_client_id",
                schema: "notifications",
                table: "applications",
                column: "client_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_applications_key",
                schema: "notifications",
                table: "applications",
                column: "key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_due",
                schema: "notifications",
                table: "deliveries",
                column: "next_at",
                filter: "next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_tenant_id_integration_id",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "tenant_id", "integration_id" });

            migrationBuilder.CreateIndex(
                name: "ix_deliveries_tenant_id_status_created_at",
                schema: "notifications",
                table: "deliveries",
                columns: new[] { "tenant_id", "status", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_tenant_id_delivery_id_number",
                schema: "notifications",
                table: "delivery_attempts",
                columns: new[] { "tenant_id", "delivery_id", "number" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_attempts_tenant_id_integration_id",
                schema: "notifications",
                table: "delivery_attempts",
                columns: new[] { "tenant_id", "integration_id" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_items_tenant_id_delivery_id",
                schema: "notifications",
                table: "delivery_items",
                columns: new[] { "tenant_id", "delivery_id" });

            migrationBuilder.CreateIndex(
                name: "ix_delivery_items_tenant_id_recipient_id_destination_key_chann",
                schema: "notifications",
                table: "delivery_items",
                columns: new[] { "tenant_id", "recipient_id", "destination_key", "channel" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integration_targets_tenant_id_integration_id",
                schema: "notifications",
                table: "integration_targets",
                columns: new[] { "tenant_id", "integration_id" });

            migrationBuilder.CreateIndex(
                name: "ix_integration_targets_tenant_id_key",
                schema: "notifications",
                table: "integration_targets",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_integrations_tenant_id_key",
                schema: "notifications",
                table: "integrations",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_due",
                schema: "notifications",
                table: "notification_recipients",
                column: "next_at",
                filter: "next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notification_recipients_tenant_id_notification_id",
                schema: "notifications",
                table: "notification_recipients",
                columns: new[] { "tenant_id", "notification_id" });

            migrationBuilder.CreateIndex(
                name: "ix_notification_types_application_id_key_version",
                schema: "notifications",
                table: "notification_types",
                columns: new[] { "application_id", "key", "version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_due",
                schema: "notifications",
                table: "notifications",
                column: "next_at",
                filter: "next_at IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_notifications_tenant_id_request_id",
                schema: "notifications",
                table: "notifications",
                columns: new[] { "tenant_id", "request_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_event_id",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "event_id");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_deliveries_publisher_key",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "publisher_key");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Pending",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "creation_time",
                filter: "state = 0")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Recovery",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "processed_on",
                filter: "state = 1")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "IX_OutboxDeliveries_Retry",
                schema: "notifications",
                table: "outbox_deliveries",
                column: "next_attempt_on",
                filter: "state = 3 AND next_attempt_on IS NOT NULL")
                .Annotation("Npgsql:IndexInclude", new[] { "event_id", "publisher_key" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_events_transaction_id",
                schema: "notifications",
                table: "outbox_events",
                column: "transaction_id");

            migrationBuilder.CreateIndex(
                name: "ix_requests_application_id_tenant_id",
                schema: "notifications",
                table: "requests",
                columns: new[] { "application_id", "tenant_id" });

            migrationBuilder.CreateIndex(
                name: "ix_requests_tenant_id_accepted_at",
                schema: "notifications",
                table: "requests",
                columns: new[] { "tenant_id", "accepted_at" });

            migrationBuilder.CreateIndex(
                name: "ix_requests_tenant_id_application_id_correlation_key",
                schema: "notifications",
                table: "requests",
                columns: new[] { "tenant_id", "application_id", "correlation_key" });

            migrationBuilder.CreateIndex(
                name: "ix_requests_tenant_id_application_id_idempotency_key",
                schema: "notifications",
                table: "requests",
                columns: new[] { "tenant_id", "application_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_requests_type_id",
                schema: "notifications",
                table: "requests",
                column: "type_id");

            migrationBuilder.CreateIndex(
                name: "ix_routes_tenant_id_key",
                schema: "notifications",
                table: "routes",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_templates_type_id_channel_locale_tenant_id_version",
                schema: "notifications",
                table: "templates",
                columns: new[] { "type_id", "channel", "locale", "tenant_id", "version" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "delivery_attempts",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "delivery_items",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "destination_allowlist",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "integration_targets",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "outbox_deliveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "routes",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "templates",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "deliveries",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notification_recipients",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "outbox_events",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "integrations",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "requests",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "application_tenants",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "notification_types",
                schema: "notifications");

            migrationBuilder.DropTable(
                name: "applications",
                schema: "notifications");
        }
    }
}
