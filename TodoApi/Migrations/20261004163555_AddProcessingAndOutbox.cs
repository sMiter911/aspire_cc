using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TodoApi.Migrations
{
    /// <inheritdoc />
    public partial class AddProcessingAndOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "last_attempt_at",
                table: "todos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processed_at",
                table: "todos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_error",
                table: "todos",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "processing_status",
                table: "todos",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "COMPLETED");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "processing_updated_at",
                table: "todos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "queued_at",
                table: "todos",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "worker_id",
                table: "todos",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    exchange = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    routing_key = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    event_type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_todos_processing_status",
                table: "todos",
                column: "processing_status");

            migrationBuilder.CreateIndex(
                name: "ix_outbox_unpublished",
                table: "outbox_messages",
                column: "created_at",
                filter: "published_at IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "ix_todos_processing_status",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "last_attempt_at",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "processed_at",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "processing_error",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "processing_status",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "processing_updated_at",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "queued_at",
                table: "todos");

            migrationBuilder.DropColumn(
                name: "worker_id",
                table: "todos");
        }
    }
}
