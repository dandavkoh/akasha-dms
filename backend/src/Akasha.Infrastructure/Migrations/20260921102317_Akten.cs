using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Akasha.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Akten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "akten",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    description = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_akten", x => x.id);
                    table.ForeignKey(
                        name: "fk_akten_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "akte_documents",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    akte_id = table.Column<Guid>(type: "uuid", nullable: false),
                    document_id = table.Column<Guid>(type: "uuid", nullable: false),
                    linked_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_akte_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_akte_documents_akten_akte_id",
                        column: x => x.akte_id,
                        principalTable: "akten",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_akte_documents_documents_document_id",
                        column: x => x.document_id,
                        principalTable: "documents",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_akte_documents_tenants_tenant_id",
                        column: x => x.tenant_id,
                        principalTable: "tenants",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_akte_documents_akte_id",
                table: "akte_documents",
                column: "akte_id");

            migrationBuilder.CreateIndex(
                name: "ix_akte_documents_document_id",
                table: "akte_documents",
                column: "document_id");

            migrationBuilder.CreateIndex(
                name: "ix_akte_documents_tenant_id_akte_id_document_id",
                table: "akte_documents",
                columns: new[] { "tenant_id", "akte_id", "document_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_akte_documents_tenant_id_document_id",
                table: "akte_documents",
                columns: new[] { "tenant_id", "document_id" });

            migrationBuilder.CreateIndex(
                name: "ix_akten_tenant_id_name",
                table: "akten",
                columns: new[] { "tenant_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "akte_documents");

            migrationBuilder.DropTable(
                name: "akten");
        }
    }
}
