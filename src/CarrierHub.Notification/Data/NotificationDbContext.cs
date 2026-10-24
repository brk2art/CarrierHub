using Microsoft.EntityFrameworkCore;

namespace CarrierHub.Notification.Data;

// A merchant's webhook. One per merchant.
public sealed class WebhookSubscription
{
    public string MerchantId { get; set; } = "";
    public string Url { get; set; } = "";
    public string Secret { get; set; } = "";   // signs deliveries; stored as-is for now (see the ADR)
    public DateTimeOffset CreatedAt { get; set; }
}

// One attempt to deliver one message to one merchant.
public sealed class Delivery
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string MessageId { get; init; }   // the outbox message: delivered successfully at most once
    public required string MerchantId { get; init; }
    public required string Type { get; init; }
    public required string Url { get; init; }
    public int? StatusCode { get; set; }
    public bool Succeeded { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset AttemptedAt { get; init; }
}

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<WebhookSubscription> Webhooks => Set<WebhookSubscription>();
    public DbSet<Delivery> Deliveries => Set<Delivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var webhook = modelBuilder.Entity<WebhookSubscription>();
        webhook.ToTable("Webhooks");
        webhook.HasKey(w => w.MerchantId);
        webhook.Property(w => w.MerchantId).HasMaxLength(64);
        webhook.Property(w => w.Url).HasMaxLength(2000);
        webhook.Property(w => w.Secret).HasMaxLength(128);

        var delivery = modelBuilder.Entity<Delivery>();
        delivery.ToTable("Deliveries");
        delivery.HasKey(d => d.Id);
        delivery.Property(d => d.Id).ValueGeneratedNever();
        delivery.Property(d => d.MessageId).HasMaxLength(100);
        delivery.Property(d => d.MerchantId).HasMaxLength(64);
        delivery.Property(d => d.Type).HasMaxLength(100);
        delivery.Property(d => d.Url).HasMaxLength(2000);
        delivery.Property(d => d.Error).HasMaxLength(1000);
        // A message can succeed only once: the database refuses a second successful delivery of it.
        delivery.HasIndex(d => d.MessageId).IsUnique().HasFilter("\"Succeeded\" = true");
        delivery.HasIndex(d => new { d.MerchantId, d.AttemptedAt });
    }
}