using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akasha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackgroundJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "background_jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    correlation_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    initiated_by_user_id = table.Column<Guid>(type: "uuid", nullable: true),
                    initiated_by_subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    initiated_by_provider = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    initiated_by_is_service_account = table.Column<bool>(type: "boolean", nullable: false),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    attempt_count = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    cancellation_requested = table.Column<bool>(type: "boolean", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    scheduled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_background_jobs", x => x.id);
                    table.ForeignKey(
                        name: "fk_background_jobs_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_status_scheduled_at_utc",
                table: "background_jobs",
                columns: new[] { "status", "scheduled_at_utc" });

            migrationBuilder.CreateIndex(
                name: "ix_background_jobs_tenant_id_type_idempotency_key",
                table: "background_jobs",
                columns: new[] { "tenant_id", "type", "idempotency_key" },
                unique: true,
                filter: "idempotency_key IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "background_jobs");
        }
    }
}
