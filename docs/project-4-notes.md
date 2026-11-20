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

## Open items
- [ ] Build the chat page (OpsAssistant.Web) into wwwroot before the OpsAssistant image is built;
      wwwroot is gitignored.
- [ ] Health checks that test Postgres, so Container Apps knows when a service is not working.

## Numbers so far
| Measure | Value |
|---|---|
| Budget | $10/month, alerts at $5, $8 and forecast $10 |
| Tests after the update | 58/58 |
| Tracking image size | 384 MB → 258 MB (−33%) |
| Azure cost so far | $0 (nothing deployed yet) |