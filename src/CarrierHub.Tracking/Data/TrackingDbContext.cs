using CarrierHub.Tracking.Domain;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CarrierHub.Tracking.Data;

public sealed class TrackingDbContext(DbContextOptions<TrackingDbContext> options) : DbContext(options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public DbSet<Shipment> Shipments => Set<Shipment>();
    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    // Every save turns the shipments' domain events into outbox rows first, so the change and its
    // announcement are written in one transaction. There's no other way to save, so nothing can forget it.
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        AddOutboxMessages();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void AddOutboxMessages()
    {
        var shipments = ChangeTracker.Entries<Shipment>()
            .Select(e => e.Entity)
            .Where(s => s.DomainEvents.Count > 0)
            .ToList();

        // The span that caused this change: the HTTP request or the Service Bus message being processed.
        var traceParent = Activity.Current is { IdFormat: ActivityIdFormat.W3C } current ? current.Id : null;

        foreach (var shipment in shipments)
        {
            foreach (var domainEvent in shipment.DomainEvents)
            {
                Outbox.Add(new OutboxMessage
                {
                    Type = domainEvent.GetType().Name,
                    Payload = JsonSerializer.Serialize(domainEvent, domainEvent.GetType(), Json),
                    SessionId = shipment.Id.ToString(),
                    CreatedAt = domainEvent switch
                    {
                        ShipmentCreated c => c.CreatedAt,
                        ShipmentStatusChanged c => c.ChangedAt,
                        _ => DateTimeOffset.UtcNow,
                    },
                    TraceParent = traceParent,
                });
            }

            shipment.ClearDomainEvents();
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var shipment = modelBuilder.Entity<Shipment>();

        shipment.HasKey(s => s.Id);
        shipment.Property(s => s.Id).ValueGeneratedNever();   // the domain creates it (Guid v7)
        shipment.Property(s => s.MerchantId).HasMaxLength(64);
        shipment.Property(s => s.MerchantReference).HasMaxLength(100);
        shipment.Property(s => s.Carrier).HasMaxLength(50);
        shipment.Property(s => s.TrackingNumber).HasMaxLength(100);
        shipment.Property(s => s.Status).HasConversion<string>().HasMaxLength(32);
        shipment.Ignore(s => s.Timeline);
        shipment.Ignore(s => s.DomainEvents);

        // Optimistic concurrency on PostgreSQL's xmin system column: when two requests change
        // the same shipment, the second save fails instead of overwriting. No column is added.
        shipment.Property<uint>("Version").IsRowVersion();

        // One shipment per merchant order number, so a retried create finds the first one.
        shipment.HasIndex(s => new { s.MerchantId, s.MerchantReference }).IsUnique();
        // Carrier events find their shipment by carrier and tracking number.
        shipment.HasIndex(s => new { s.Carrier, s.TrackingNumber }).IsUnique();
        // "Which of this merchant's shipments are in status X?"
        shipment.HasIndex(s => new { s.MerchantId, s.Status });

        shipment.OwnsMany(s => s.Events, e =>
        {
            e.ToTable("TrackingEvents");
            e.WithOwner().HasForeignKey("ShipmentId");
            // The key is the duplicate rule: the same carrier event can't be stored twice,
            // even when two requests race past the check in the domain.
            e.HasKey("ShipmentId", nameof(TrackingEvent.CarrierEventId));
            e.Property(x => x.CarrierEventId).HasMaxLength(128);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(32);
            e.Property(x => x.Location).HasMaxLength(200);
            e.Property(x => x.Description).HasMaxLength(500);
        });
        shipment.Navigation(s => s.Events).UsePropertyAccessMode(PropertyAccessMode.Field);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(m => m.Id);
        outbox.Property(m => m.Id).ValueGeneratedNever();
        outbox.Property(m => m.Type).HasMaxLength(100);
        outbox.Property(m => m.SessionId).HasMaxLength(100);
        outbox.Property(m => m.Payload).HasColumnType("jsonb");
        outbox.Property(m => m.LastError).HasMaxLength(1000);
        outbox.Property(m => m.TraceParent).HasMaxLength(64);   // "00-<32 hex>-<16 hex>-<2 hex>" is 55
        // Only unpublished rows are indexed: the publisher's query stays fast however large the table grows.
        outbox.HasIndex(m => m.CreatedAt).HasFilter("\"PublishedAt\" IS NULL");
    }
}