# Project 4 notes: CarrierHub on Azure

Working notes. The README and the post are written from these at the end.

## Week 1: Ready to deploy

### Budget alert (Bicep)
- What: a monthly $10 budget on the subscription, written in Bicep (`ops/budget.bicep`).
- Alerts: 50% and 80% of actual spend, 100% of forecast spend. The forecast alert is the
  most useful one, because it warns before the money is spent.
- The contact email lives in `ops/budget.bicepparam`, which is gitignored.
- Ran `what-if` before deploying: 1 resource to create, nothing else.
- A budget only warns; it never stops spending. What keeps the cost down is `azd down --purge`
  after every session.

### Aspire update and a security fix
- Planned: 13.6.3 to 13.6.4. NuGet still flagged 13.6.4 with a high-severity advisory
  (CVE-2026-120006, Aspire elevation of privilege). Fixed in 13.6.5.
- 13.6.5 needs EF Core 10.0.14, so restore failed with NU1605 (package downgrade).
  Moved EF Core and ASP.NET Core from 10.0.13 to 10.0.14 as well.
- Gotcha: Visual Studio's Replace All also changed `ProductVersion` in 10 EF migration files.
  I reverted them, because they record which EF version created each migration.
- Result: 0 warnings, 58/58 tests pass. The Tracking tests need Docker running (Testcontainers).

### azd init
- azd 1.39.1, "Scan current directory". It found the AppHost and created `azure.yaml` with
  one service pointing at the AppHost. azd asks the AppHost for the real list of services.
- Not deployed, because the AppHost doesn't reference them: PolicyIndexer (a local tool),
  McpServer (stdio, runs on the developer's machine), OpsAssistant.Web (the chat page,
  built into OpsAssistant's wwwroot).
- "Limited mode": azd owns the Container Apps environment, so it's customized in Bicep
  (`azd infra gen`), not in the AppHost.
- Environment `carrierhub-dev`; the `.azure/` folder is gitignored.

### Container images
- No Dockerfile: `dotnet publish /t:PublishContainer` builds the image. azd does the same.
- Tracking image: 384 MB on the default base, 258 MB on chiseled Ubuntu (`noble-chiseled-extra`).
  That's 33% smaller, with no shell and no package manager, running as non-root.
- Set once for all services in `Directory.Build.props` (`ContainerFamily`).
- Trade-off: no shell inside the container, so debugging goes through logs and traces.
- Finding: the container started fine with no database configured. The connection is only used
  on the first request. "Started" is not "working", so health checks must test the database
  (Week 4).

## Week 2: Infrastructure as code

### azd infra gen
- Generated 10 container apps (7 services + postgres + redis) and a real Azure Service Bus
  (the emulator is local only).
- Default Postgres in Azure = a container with its data on an Azure Files share, minReplicas 1.
  Fragile for Postgres (network file share), no backups, password auth.
  Decision: Azure Database for PostgreSQL Flexible Server B1ms instead; local stays a container.
- Switched the AppHost to `AddAzurePostgresFlexibleServer("postgres").RunAsContainer(...)`:
  Flexible Server in Azure, the same Docker container locally (same data: 8,438 shipments).
- infra gen now also creates postgres-roles: the services log in to Postgres with their
  managed identity (Entra ID), no password.
- Flexible Server defaults are already demo-cheap: B1ms Burstable, 32 GB, no high availability,
  7-day backups, Postgres 16. Password login disabled, Entra only.
- Firewall `AllowAllAzureIps` lets any Azure service reach it over the network. Acceptable because
  only Entra identities can log in; production would use a VNet + private endpoint (costs more).
- The connection string is `Host=...` only, so the services need an Entra token to log in.
- `infra/` is committed: from now on, running `azd infra gen` again overwrites manual edits.
- Checked the generated files for secrets before committing: every value is a placeholder
  (`securedParameter`, `.Env`), so nothing secret reaches GitHub.
- resources.bicep: container registry Basic, Log Analytics pay-per-GB, Container Apps environment
  Consumption only (nothing bills while idle), Aspire dashboard included.
- One managed identity shared by all services: simple, but not least privilege. Production would
  give each service its own identity.
- Service Bus Standard is required, not a choice: Basic has no sessions, duplicate detection or topics.
  disableLocalAuth = true, so Entra only, no keys.
- Estimated cost: about $0.30 per 3-hour session (Postgres ~$0.06, Service Bus ~$0.04, registry ~$0.17).

### Entra login for Postgres
- Swapped `Aspire.Npgsql.EntityFrameworkCore.PostgreSQL` for `Aspire.Azure.Npgsql.EntityFrameworkCore.PostgreSQL`
  in 3 services, and `AddNpgsqlDbContext` for `AddAzureNpgsqlDbContext`.
- The same code works in both places: with a password in the connection string (local Docker,
  Testcontainers) it uses the password; without one (Azure) it gets an Entra token from the
  managed identity and refreshes it.
- Design-time DbContext factories (`Host=localhost`) are only used by `dotnet ef`, so they stay.
- 58/58 tests pass.

## Open items
- [ ] Build the chat page (OpsAssistant.Web) into wwwroot before the OpsAssistant image is built;
      wwwroot is gitignored.
- [ ] Health checks that test Postgres, so Container Apps knows when a service is not working.
- [ ] OpsAssistant in Azure gets only assistantdb: Azure OpenAI, AI Search, Content Safety and the
      sweep token live in local user secrets, which don't travel. Plan: managed identity + roles for
      the Azure AI services (no keys), secret parameter from the AppHost for the sweep token.
- [x] Postgres login: the services must use Entra tokens (managed identity), not a password.
- [ ] Migrations run at startup only in Development, so the Azure databases would have no tables.

## Numbers so far
| Measure | Value |
|---|---|
| Budget | $10/month, alerts at $5, $8 and forecast $10 |
| Tests after the update | 58/58 |
| Tracking image size | 384 MB → 258 MB (−33%) |
| Azure cost so far | $0 (nothing deployed yet) |
| Estimated cost per 3-hour session | ~$0.30 |