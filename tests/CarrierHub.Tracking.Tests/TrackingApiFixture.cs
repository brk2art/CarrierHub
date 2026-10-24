using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;

namespace CarrierHub.Tracking.Tests;

// One PostgreSQL container and one running app, shared by all tests in a class.
public sealed class TrackingApiFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18.3")   // the same version Aspire runs
        .Build();

    private WebApplicationFactory<Program>? _factory;

    // A clock the tests control. It only ever moves forward.
    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 20, 8, 0, 0, TimeSpan.Zero));

    public HttpClient Client { get; private set; } = null!;

    public IServiceProvider Services => _factory!.Services;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(web =>
        {
            web.UseEnvironment("Development");   // Program applies migrations at startup in Development
            web.UseSetting("ConnectionStrings:trackingdb", _postgres.GetConnectionString());   // replaces what Aspire injects
            web.ConfigureTestServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
            web.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        });

        Client = _factory.CreateClient();   // starts the app, so the migrations run against the container
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}