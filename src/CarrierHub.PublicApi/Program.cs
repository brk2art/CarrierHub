using CarrierHub.PublicApi;
using CarrierHub.PublicApi.V1;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenApi();

// Validates bearer tokens with the settings under "Authentication:Schemes:Bearer".
// In development, dotnet user-jwts writes those settings and keeps the signing key in user secrets.
builder.Services.AddAuthentication().AddJwtBearer();

builder.Services.AddAuthorizationBuilder()
    // Reading needs a merchant and either scope; writing needs a merchant and the write scope.
    .AddPolicy(Policies.Read, p => p
        .RequireClaim(Claims.MerchantId)
        .RequireClaim("scope", Scopes.Read, Scopes.Write))
    .AddPolicy(Policies.Write, p => p
        .RequireClaim(Claims.MerchantId)
        .RequireClaim("scope", Scopes.Write));

builder.Services.AddHttpClient<TrackingClient>(client => client.BaseAddress = new Uri("https+http://tracking"));

var app = builder.Build();

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/", () => "CarrierHub Public API");
app.MapShipmentEndpoints();
app.MapDefaultEndpoints();

app.Run();

public partial class Program { }   // for integration tests