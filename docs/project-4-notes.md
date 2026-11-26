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

### Migrations in Azure
- Before: migrations ran at startup only in Development, so the Azure databases would have no tables.
- Now: `Database:MigrateOnStartup` (set to true by the AppHost for tracking, notification and
  opsassistant) also turns it on outside Development. Missing setting = false = no migration.
- Chosen for the demo because it's simple and works with azd up. The identity needs schema rights
  (it's the Entra admin). EF Core 9+ locks the database while migrating, so parallel replicas are safe.
- Production answer: an EF migration bundle in the deployment pipeline, before the new version starts.
- Regenerating infra showed exactly 3 changed files, one env var each: committed infra makes
  the effect of an AppHost change visible in `git diff`.

### First azd up
- West Europe failed validation: "region is currently not accepting new customers" for every
  regional resource. Nothing was created ($0). Content Safety had hit the same restriction.
- Moved the environment to Sweden Central (`azd env set AZURE_LOCATION swedencentral`) and ran
  `azd provision --preview` before deploying.
- Sweden Central: provisioning succeeded. Postgres Flexible Server took 6 min 27 s;
  the registry, Log Analytics, Service Bus and the Container Apps environment took 1–2 min each, in parallel.
- SUCCESS in 15 min 36 s (provisioning 10:32, deploying 6:34: 7 images built and pushed in parallel).
- Only publicapi has a public URL; every other service is `.internal` (reachable only inside the
  Container Apps environment), including OpsAssistant.
- From the internet: `GET /v1/shipments` without a token → 401 with `www-authenticate: Bearer`.
  The API runs in Azure behind HTTPS and refuses anonymous calls.
- Small hardening item: the response shows `server: Kestrel`; turn the server header off (Week 4).
- Tracking in Azure: logged in to Postgres with the managed identity (Entra token) and applied all
  4 migrations at startup. Both fixes confirmed.
- Npgsql logs "Cannot load library libgssapi_krb5.so.2": the chiseled image has no Kerberos library.
  Harmless (it carries on), but noise to turn off later.
- Startup probe failed while Tracking was migrating, then recovered (Week 4: health checks).
- OpsAssistant crash-looped: "Set Assistant:AzureOpenAI:Endpoint" (settings are in local user secrets).
- azd up said SUCCESS and the dashboard said Running while OpsAssistant had 0/1 replicas ready.
  Neither checks that the app works, so the pipeline needs a smoke test after deploying (Week 5).
- Images pulled from the registry: opsassistant 81.8 MB, tracking 77.6 MB (compressed; 258 MB on disk).
- `azd down --purge`: 16 resources deleted (Log Analytics purged) in 25 min 50 s.
  Session overhead ≈ 40 min (16 up + 26 down), so plan several tasks per deployed session.
- Real cost of the first session: check Cost analysis the next day (it lags ~24 h).

## Week 3: Identity and secrets

### Keyless AI access
- OpsAssistant's Azure OpenAI and AI Search clients already used "key if set, else DefaultAzureCredential".
- PromptShield required a key: without one it returned null, so in Azure the prompt-injection shield
  would have been silently OFF (it fails open). Now it sends an Entra token (BearerTokenHandler,
  refreshed 5 minutes before expiry) when there's no key.
- McpHttp runs in Azure too and used keys only. Now key-or-identity for Search and embeddings,
  with one shared DefaultAzureCredential. Found a bug on the way: it checked searchKey for the OpenAI client.
- AI Search already allowed Entra (`aadOrApiKey`); all 3 AI resources live in rg-docchat-dev,
  so CarrierHub depends on DocChat's resource group.
- `WithRoleAssignments` failed in azd's limited mode ("the application model does not support role
  assignments"). Limited mode only supports the default roles that come with WithReference, and
  Search's defaults include write access. Left limited mode with `AddAzureContainerAppEnvironment("env")`:
  the AppHost now owns the Container Apps environment.
- Role IDs checked in the generated Bicep: OpenAI User (5e0bd9bd), Search Index Data Reader (1407120a),
  Cognitive Services User (a97b65f3). OpsAssistant can use the AI resources, not change them.
- Aspire sets AZURE_TOKEN_CREDENTIALS, so in Azure DefaultAzureCredential only tries the managed identity.
- Secret scan: values come from securedParameter into @secure() Bicep params, so they're never
  shown in the portal's deployment history.
- Caught by reading the diff: McpHttp's guard still required both keys, so the new identity code would
  never have run. A change can compile and still be dead code.

### Sign-in in Azure
- The Public API had two schemes: dev tokens (dotnet user-jwts, signed with a key on my laptop)
  and Entra tokens. The dev scheme was registered in every environment; in Azure it refused
  everything only because its signing keys happened to be missing: safe by accident.
- Now dev tokens are only registered outside Production, and Production without Entra settings
  refuses to start with a clear message.
- Checked locally: dev token 200, no token 401, made-up token 401, and Production mode
  without Entra settings stops at startup (tested in 30 seconds instead of a 40-minute deploy).
- Gotcha: `dotnet run` applies launchSettings.json, which sets Development and overrides
  $env:ASPNETCORE_ENVIRONMENT. Use `--no-launch-profile` to really run as Production.
- Tested the whole Entra chain locally for the first time: Azure CLI token → chooser → Entra scheme
  (signature, issuer, audience, lifetime) → claims transformation (oid → merchant-1,
  Shipments.Read → shipments:read) → policies. /v1/me 200, read 200, write 403.
- Two 401s on the way were an expired token (IDX10223), sent from a stale value in the env file.
  Visual Studio caches the env file: switch the env dropdown or reopen the file after editing it.
- To get an Entra token for testing: `az account get-access-token --scope api://<id>/Shipments.Read`.
  It first worked through an earlier consent, not the "authorized client applications" list, which
  turned out to be empty. Added the Azure CLI (04b07795-8ddb-461a-bbee-02f9e1bf7b46) there explicitly,
  with both scopes, so the setup doesn't depend on an old consent. The token lasts ~60–90 min.
- The registration only exposed Shipments.Read, although ScopeMap also knew Shipments.Write: Entra users
  could read but never create. Locally it never showed, because dev tokens have both scopes.
  Added the Shipments.Write scope, needed to put data into the empty Azure database.
- Merchant mapping changed from Entra:Merchants:<oid> = merchant to a list Entra:MerchantUsers:N:{ObjectId, MerchantId}:
  in Azure the object ID comes from an azd parameter, which can fill a setting's value but not its name.
  Proven: new code + old secrets → 403 (signed in, no merchant, never a default); new secrets → 200.
- Reliable Entra test: `curl.exe -H "Authorization: Bearer $entraToken" https://localhost:<port>/v1/me` in the
  same PowerShell window as `az account get-access-token`. Avoids Visual Studio's cached env file.
- In Azure, the Public API gets its Entra settings (tenant, client ID, the merchant user's object ID) from
  3 azd parameters: main.parameters.json only holds references like ${AZURE_ENTRA_TENANT_ID}; the values
  live in .azure/. Not secrets, but personal, so not in the repo. Regenerating changed only the publicapi module.

### Exposing OpsAssistant
- Before giving OpsAssistant a public URL, checked every endpoint it maps. A2A and AG-UI have no sign-in,
  so they're now Development-only. Actions (list/approve) refuse calls without a token: 401.
- Found with a trace: `ask` with a made-up token ran the model, called a tool (which got 401), ran
  Content Safety, ran the model again, and only then returned 401. The token was checked by the first
  tool, not at the door: anyone could spend my Azure OpenAI budget ("denial of wallet").
  The budget alert would warn, but not stop it.
- Only the streaming endpoint checked /v1/me first; ask and lateness relied on the tools to find a bad
  token. Fixed with an endpoint filter on the /v1/assistant group: every AI endpoint checks the caller
  before its handler, so a new endpoint can't forget it.
- Measured with a made-up token: 1.85 s with 2 model calls + Content Safety → 0.04 s with no model call.
- The chat page is built into OpsAssistant's wwwroot (gitignored) by an azd `prepackage` hook in azure.yaml:
  `npm ci && npm run build`, with a windows (pwsh) and a posix (sh) variant for GitHub Actions later.
  Vite's emptyOutDir clears old hashed files first. Tested with `azd hooks run prepackage`, no deployment.
- Installed PowerShell 7: azd hooks on Windows need pwsh, and it supports `&&`.
- `.WithExternalHttpEndpoints()` on opsassistant: regenerating changed exactly one line in Azure
  (ingress external: false → true). Done only after every endpoint was checked.
- azd infra gen never deletes files it no longer generates: regenerate from scratch
  (delete infra/ and the AppHost's infra/), and let `git status` show what disappeared.

### Second azd up: everything tested in Azure
- 16 min up. All apps Healthy, including OpsAssistant (crash-looped last time).
- Anonymous calls on the public URLs: ask with a made-up token 401 in 0.33 s (no model), approve 401,
  AG-UI 405 (not mapped in Production), Public API 401.
- Entra token against the Public API in Azure: 200, merchant-1, both scopes (the 3 azd parameters work).
- Created AZURE-1 and AZURE-2 with the Entra token; ~2 min later both were Delivered, AZURE-1 late by 70 s:
  Gateway → MockCarriers → Service Bus (identity, no keys) → Tracking → Postgres (outbox).
- Chat page in Azure: the NordPost terms question answered with its source (Azure OpenAI + AI Search, keyless),
  and "Which of my shipments are late?" → AZURE-1 only.
- Content Safety in Azure: no shield warnings in the logs, but not yet confirmed by its trace span (open item).
- McpHttp crash-loops on purpose without its Entra settings (open item: exclude it from Azure).
- `azd down` failed halfway when the laptop went offline (DNS lookup of login.microsoftonline.com failed):
  only Log Analytics was purged, everything else kept running for hours. Lesson: run azd down while
  staying online, and always confirm with `az group exists --name rg-carrierhub-dev` → false.

## Week 4: Production readiness

### Health checks
- /alive (liveness: only "self") and /health (readiness: what the service can't work without) are now mapped in
  every environment, so Container Apps can probe them. Safe outside Development: plain status only (no names or
  errors), /health cached 10 s, /alive checks nothing external.
- Liveness must never include a dependency: in a Postgres outage, every replica would be restarted at once.
- Tracking's readiness = TrackingDbContext only. No Service Bus check, and that's right: the outbox keeps
  Tracking working while the bus is down (measured in Project 3: messages delivered 68 s later).
- Postgres stopped: /health 503, /alive 200, and Tracking recovered by itself when Postgres came back (200 in
  0.02 s, no restart). The database check first took 163 s (EF retry-on-failure from the Aspire integration).
  A 3 s timeout on every check stopped the retries: 15 s, which is Npgsql's own connection timeout (the connect
  step doesn't react to cancellation). Kept: the probe decides in 5 s anyway, and at most ~2 checks are in
  flight per replica. Lowering Npgsql's timeout would also affect real queries.

### Application Insights and the P95 alert
- Application Insights is added in the AppHost's publish-mode block (locally the Aspire dashboard is enough, and
  outside that block every local run would try to create it). The 7 services get its connection string from an
  azd environment value, not from the repo; ServiceDefaults calls UseAzureMonitor() only when it's set.
- ops/alerts.bicep (written by hand): an action group (email from an azd parameter) and a scheduled query rule
  running KQL every 5 minutes: `requests | where cloud_RoleName == "publicapi" | summarize p95 = percentile(duration, 95)`,
  firing above 1,000 ms, auto-resolving. Included with AddBicepTemplate, wired to Aspire's resource with
  insights.GetOutput("name").
- Why P95 and not the average: 95 requests at 20 ms and 5 at 4 s average ~220 ms, which looks fine while
  1 user in 20 waits 4 seconds.

### The sweep as a job: its own identity
- Design: the job gets its OWN managed identity, not OpsAssistant's. OpsAssistant is public and only ever acts
  with the caller's token; giving it an identity that can act as merchant-1 alone would let a bug or a prompt
  injection read data with no user signed in.
- The sweep only reads and prepares proposals (a person approves with their own token), so it needs only
  Shipments.Read.
- Code: the token came from one place (options.Token), so only that changed: dev token if set (local), else an
  Entra token for the Public API (Audience/.default) from DefaultAzureCredential. Plus --sweep-once: run one sweep,
  exit 0 or 1, no web server. Local sweep unchanged: 6 found, 6 already decided, 0 tokens.
- Entra puts permissions in two claims: users get "scp" (one space-separated string, from consent), programs get
  "roles" (one claim per app role assigned to them). EntraClaimsTransformation now reads both. A program's token
  also has an oid, so it's mapped through Entra:MerchantUsers like a user. 4 unit tests, no Entra needed:
  program read-only, user unchanged, unmapped program → no merchant, unknown role → nothing. 62/62.
- App role Shipments.Read.App (allowed member types: Applications) on carrierhub-public-api. Entra refused
  "Shipments.Read": a value must be unique across the app's scopes AND app roles. ScopeMap maps it to the same
  CarrierHub scope (shipments:read).
- The hook then failed with "-o was unexpected at this time": on Windows az is a batch file (az.cmd), so its
  arguments pass through cmd, which misreads parentheses in a JMESPath query. Fixed by returning the matching
  IDs and checking in PowerShell instead of length(...) in the query.

### Small hardening
- `server: Kestrel` removed from every response (AddServerHeader = false in ServiceDefaults): one less hint
  for an attacker, and scanners flag it.
- Npgsql's Kerberos attempt turned off explicitly (GssEncryptionMode.Disable in the 3 services): Entra signs in
  and TLS encrypts, so each connection no longer tries to load a library the chiseled image doesn't have.

### Week 4 test deployment
- Provision: the Container Apps environment took 15 min this time (under 2 min before): Azure-side variation.
- First deploy failed on MockCarriers with CONTAINER1013 "Access to the path is denied": a local race in .NET's
  container layer cache while azd builds all images in parallel. `azd deploy` again worked.
- Sweep job in Azure, started by hand: Succeeded in ~35 s. "2 late, 0 already decided, 2 checked: 1 proposed,
  1 skipped, 0 failed, 14,485 tokens". It signed in as its own managed identity (app role Shipments.Read.App,
  mapped to merchant-1); the proposal waits for a person.
- The job reaches the Public API at its public address, not an internal one: works, still authenticated;
  internal calls should stay internal (later).
- Probes: no probe failures, 7 × Healthy. No gssapi line. No server header in Azure either.
- Its proposal in Azure: "Open a Late claim with ParcelGo for AZURE-4", Pending, expires in 24 h, audit trail shows the sweep that proposed it.
- Azure Monitor emails only verified addresses: each new action group sends a "Verify your email" message first,
  and nothing is delivered until it's clicked. Because azd down deletes the action group, every azd up asks again.
  In production the action group would live outside the torn-down environment, verified once.
- With the corrected filter, the alert fired at 18:28 for traffic sent at 18:15: about 13 min from slowness to
  alert (telemetry ingestion + the 5-minute evaluation). Delivered to the verified address.
- Content Safety in Azure: HTTP POST 200 to carrierhub-contentsafety, 0.18 s, with OpsAssistant's own identity.
  Week 3's open item closed.
- The trace shows the keyless design: DefaultAzureCredential.GetToken → localhost:12356 (the managed identity
  endpoint inside the container), then Azure OpenAI, AI Search and Content Safety with tokens. The first token
  took ~1 s, cached ones 0.5 ms.
- A wrong token in the chat page: 0.3 s, no model spans (the endpoint filter, confirmed in Azure).
- Small: /v1/me is called twice per question (the filter and the assistant). The filter could pass its result on.

## Open items
- [ ] Build the chat page (OpsAssistant.Web) into wwwroot before the OpsAssistant image is built;
      wwwroot is gitignored.
- [ ] Health checks that test Postgres, so Container Apps knows when a service is not working.
- [ ] OpsAssistant in Azure gets only assistantdb: Azure OpenAI, AI Search, Content Safety and the
      sweep token live in local user secrets, which don't travel. Plan: managed identity + roles for
      the Azure AI services (no keys), secret parameter from the AppHost for the sweep token.
- [x] Postgres login: the services must use Entra tokens (managed identity), not a password.
- [x] Migrations run at startup only in Development, so the Azure databases would have no tables.
- [ ] McpHttp in Azure: excluded with ExcludeFromManifest() (it crash-looped on purpose without its Entra
      settings, and nothing in Azure calls it). Add it back with tenant, client ID, client secret for
      on-behalf-of and Mcp:ResourceUrl when an MCP client should reach it in Azure.
- [ ] Sweep token in Azure: not needed while sweeps are Development-only; comes with the
      Container Apps job (Week 4).

## Numbers so far
| Measure | Value |
|---|---|
| Budget | $10/month, alerts at $5, $8 and forecast $10 |
| Tests after the update | 58/58 |
| Tracking image size | 384 MB → 258 MB (−33%) |
| Azure cost so far | $0 (nothing deployed yet) |
| Estimated cost per 3-hour session | ~$0.30 |
| azd up (first deploy) | 15 min 36 s |
| azd down --purge | 25 min 50 s |