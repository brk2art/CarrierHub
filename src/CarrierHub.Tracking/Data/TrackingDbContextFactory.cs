using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CarrierHub.Tracking.Data;

// Used only by `dotnet ef` to create migrations. It never connects, so the connection string is a placeholder.
// Without it, dotnet ef would start the app, which needs the connection string that only Aspire provides.
public sealed class TrackingDbContextFactory : IDesignTimeDbContextFactory<TrackingDbContext>
{
    public TrackingDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TrackingDbContext>()
            .UseNpgsql("Host=localhost;Database=trackingdb")
            .Options);
}