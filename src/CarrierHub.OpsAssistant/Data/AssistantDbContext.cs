using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Reflection.Emit;

namespace CarrierHub.OpsAssistant.Data;

// The assistant's own data: what it prepared for approval, and what happened to it. No shipment data: that stays
// in Tracking, reached only through the Public API.
public sealed class AssistantDbContext(DbContextOptions<AssistantDbContext> options) : DbContext(options)
{
    public DbSet<PendingAction> PendingActions => Set<PendingAction>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var action = modelBuilder.Entity<PendingAction>();
        action.ToTable("PendingActions");
        action.HasKey(a => a.Id);
        action.Property(a => a.Id).ValueGeneratedNever();   // created by NewClaim (Guid v7)
        action.Property(a => a.Kind).HasMaxLength(32);
        action.Property(a => a.MerchantId).HasMaxLength(64);
        action.Property(a => a.ConversationId).HasMaxLength(64);
        action.Property(a => a.MerchantReference).HasMaxLength(100);
        action.Property(a => a.Carrier).HasMaxLength(50);
        action.Property(a => a.Reason).HasMaxLength(32);
        action.Property(a => a.Note).HasMaxLength(500);
        action.Property(a => a.IdempotencyKey).HasMaxLength(100);
        action.Property(a => a.Status).HasConversion<string>().HasMaxLength(32);
        action.Property(a => a.DecidedBy).HasMaxLength(200);
        action.Property(a => a.Error).HasMaxLength(1000);
        action.Ignore(a => a.Summary);

        // Optimistic concurrency on PostgreSQL's xmin, as for Tracking's shipments: when two requests change the
        // same action, the second save fails instead of overwriting. No column is added.
        action.Property<uint>("Version").IsRowVersion();

        // One key, one action: a second safety net next to Tracking's own unique index.
        action.HasIndex(a => a.IdempotencyKey).IsUnique();
        // "This merchant's actions, newest first" and "is this claim already proposed?"
        action.HasIndex(a => new { a.MerchantId, a.ProposedAt });
        action.HasIndex(a => new { a.MerchantId, a.ShipmentId, a.Reason });

        var audit = modelBuilder.Entity<AuditEntry>();
        audit.ToTable("AuditEntries");
        audit.Property<long>("Id");   // a key the record doesn't need to know about; also the order lines were written in
        audit.HasKey("Id");
        audit.Property(e => e.MerchantId).HasMaxLength(64);
        audit.Property(e => e.Event).HasMaxLength(64);
        audit.Property(e => e.Actor).HasMaxLength(200);
        audit.Property(e => e.Detail).HasMaxLength(1000);
        audit.HasIndex(e => e.ActionId);
    }
}

// Only for `dotnet ef migrations add`: EF needs a provider to build the model. It never connects.
public sealed class AssistantDbContextFactory : IDesignTimeDbContextFactory<AssistantDbContext>
{
    public AssistantDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<AssistantDbContext>()
            .UseNpgsql("Host=localhost;Database=assistantdb")
            .Options);
}