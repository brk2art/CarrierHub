using CarrierHub.Tracking.Domain;
using Microsoft.EntityFrameworkCore;

namespace CarrierHub.Tracking.Data;

public sealed class TrackingDbContext(DbContextOptions<TrackingDbContext> options) : DbContext(options)
{
    public DbSet<Shipment> Shipments => Set<Shipment>();

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
    }
}