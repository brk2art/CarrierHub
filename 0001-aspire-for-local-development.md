# 1. Aspire for local development; Helm and Terraform for deployment

Status: Accepted (Phase 1)

## Context
CarrierHub runs six services plus PostgreSQL, Redis and the Service Bus emulator. Starting and wiring
those by hand for every change is slow and error-prone. Following one carrier event across services
needs distributed tracing from day one. The project must also show production deployment skills
(Kubernetes, Helm, Terraform) that job ads ask for.

## Decision
- The Aspire AppHost starts every dependency and service locally with one command and wires them
  together: connection strings and service addresses are injected as environment variables.
- Every service calls `AddServiceDefaults()`: OpenTelemetry, health checks, service discovery and
  HttpClient resilience (retry, timeout, circuit breaker) come from one shared project.
- Aspire is not used for deployment. Phase 6 builds Docker images and deploys them with our own
  Helm charts to infrastructure defined in Terraform.

## Consequences
- One command (`dotnet run --project src/CarrierHub.AppHost`) gives a running system and a dashboard
  with logs, traces and metrics.
- Services call each other by name (`https+http://tracking`), never by hard-coded ports.
- The wiring Aspire does by itself must be done explicitly in Helm values for Kubernetes
  (`ConnectionStrings__trackingdb`, `services__tracking__https__0`).
- Docker is required for local development.
- Wiring mistakes compile. The Public API once referenced the tracking database instead of the
  Tracking service: it got a connection string it must never have, and calls failed with
  "No such host is known (tracking:443)". Found in manual testing; an AppHost test with
  Aspire.Hosting.Testing would catch it earlier.
- Integration tests don't use the AppHost: they start their own PostgreSQL with Testcontainers,
  so they run in CI without Aspire.