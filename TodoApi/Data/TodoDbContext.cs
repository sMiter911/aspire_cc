using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TodoApi.Models;

namespace TodoApi.Data;

public class TodoDbContext(DbContextOptions<TodoDbContext> options) : DbContext(options)
{
    public DbSet<Todo> Todos => Set<Todo>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Todo>(e =>
        {
            e.ToTable("todos");
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
            e.Property(t => t.Description).HasMaxLength(2000);
            e.Property(t => t.UserId).IsRequired();
            // "list my todos" (newest first) is the hot query; the admin filter by user uses the same index.
            e.HasIndex(t => new { t.UserId, t.CreatedAt }).HasDatabaseName("ix_todos_user_id_created_at");

            // Stored as the wire strings (QUEUED, WAITING_RELEASE, ...) so the column is readable in SQL.
            e.Property(t => t.ProcessingStatus)
                .HasConversion(v => v.ToWire(), v => ParseStatus(v))
                .HasMaxLength(20).IsRequired();
            e.Property(t => t.WorkerId).HasMaxLength(100);
            e.Property(t => t.ProcessingError).HasMaxLength(200);
            e.HasIndex(t => t.ProcessingStatus).HasDatabaseName("ix_todos_processing_status");
        });

        modelBuilder.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages");
            e.HasKey(m => m.Id);
            e.Property(m => m.Exchange).HasMaxLength(100).IsRequired();
            e.Property(m => m.RoutingKey).HasMaxLength(100).IsRequired();
            e.Property(m => m.EventType).HasMaxLength(100).IsRequired();
            e.Property(m => m.Payload).IsRequired();
            // The publisher scans only unpublished rows, oldest first.
            e.HasIndex(m => m.CreatedAt).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_unpublished");
        });

        // snake_case tables/columns to match the Postgres convention used by the Spring service.
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnake(property.Name));
            }
        }
    }

    private static ProcessingStatus ParseStatus(string wire) =>
        ProcessingStatuses.TryParse(wire, out var s) ? s : throw new InvalidOperationException("Unknown processing status in database");

    private static string ToSnake(string name) =>
        Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
