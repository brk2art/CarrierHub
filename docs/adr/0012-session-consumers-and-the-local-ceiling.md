\# 12. Session consumers: many slots, a short idle; the emulator is the local ceiling



\## Status

Accepted



\## Context

Tracking (carrier-events) and Notification (notifications subscription) read with one session per shipment.

Both used 8 concurrent sessions and a 2 s session idle timeout. A k6 capacity test (ParcelGo only, so the polling

budget plays no part) created shipments at 5, 10 and 20 per second, 4 minutes each.



\## Measurements

\- Public API: P95 13–16 ms at every rate, 0 errors. At 20/s the per-merchant limit (10/s) returned 429 for half the

&#x20; creates, as designed; raised for local load tests only (appsettings.Development.json).

\- 8 sessions, 2 s idle: about 2.75 webhooks/s delivered; a backlog grew from the first stage (5/s needs \~20/s).

&#x20; A session with one message keeps its slot for the full idle time: 8 slots / \~2.9 s ≈ 2.75/s.

\- 32 sessions, 0.2 s idle: about 6 webhooks/s, still behind.

\- At that point the emulator sometimes took 10 s to accept a send to carrier-events; the mock carrier's 10 s attempt

&#x20; timeout cancelled the webhook (TaskCanceledException in ServiceBusSender.Send, every \~19 s).

\- delivery\_time looked healthy while the backlog grew: it measures only messages that were delivered.



\## Decision

\- Session consumers default to 32 sessions and a 0.2 s idle timeout, configurable as

&#x20; ServiceBus:MaxConcurrentSessions and ServiceBus:SessionIdleTimeout.

\- Local load tests stay at a few shipments per second; bus capacity is measured against an Azure Service Bus

&#x20; namespace (Phase 6) with the same k6 script and metrics. The emulator is meant for sequential testing.



\## Consequences

\- Backlog must be measured directly (Service Bus ActiveMessageCount per entity, or published minus delivered), not

&#x20; inferred from the latency of finished work.

\- The Gateway holds a carrier's webhook open while the send waits; with the SDK's default send timeout a slow bus

&#x20; becomes carrier timeouts and resent webhooks. Fail-fast send with a short timeout and a 503: follow-up.

