using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akasha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Metadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "metadata_definitions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    label = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    field_type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    is_required = table.Column<bool>(type: "boolean", nullable: false),
                    max_length = table.Column<int>(type: "integer", nullable: true),
                    options = table.Column<string[]>(type: "text[]", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_definitions", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_definitions_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "metadata_values",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_type = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    owner_id = table.Column<Guid>(type: "uuid", nullable: false),
                    definition_id = table.Column<Guid>(type: "uuid", nullable: false),
                    text_value = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    number_value = table.Column<decimal>(type: "numeric", nullable: true),
                    boolean_value = table.Column<bool>(type: "boolean", nullable: true),
                    date_value = table.Column<DateOnly>(type: "date", nullable: true),
                    timestamp_value = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    selection_values = table.Column<string[]>(type: "text[]", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_metadata_values", x => x.id);
                    table.ForeignKey(
                        name: "fk_metadata_values_metadata_definitions_definition_id",
                        column: x => x.definition_id,
                        principalTable: "metadata_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_metadata_values_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_definitions_tenant_id_key",
                table: "metadata_definitions",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_metadata_values_definition_id",
                table: "metadata_values",
                column: "definition_id");

            migrationBuilder.CreateIndex(
                name: "ix_metadata_values_tenant_id_definition_id",
                table: "metadata_values",
                columns: new[] { "tenant_id", "definition_id" });

            migrationBuilder.CreateIndex(
                name: "ix_metadata_values_tenant_id_owner_type_owner_id_definition_id",
                table: "metadata_values",
                columns: new[] { "tenant_id", "owner_type", "owner_id", "definition_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "metadata_values");

            migrationBuilder.DropTable(
                name: "metadata_definitions");
        }
    }
}
