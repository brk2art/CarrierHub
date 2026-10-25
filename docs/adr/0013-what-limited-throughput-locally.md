\# 13. What limited throughput locally, and what we changed



\## Status

Accepted



\## Context

The capacity test (k6, ParcelGo only, 5 → 10 → 20 shipments/s, 4 minutes each) kept falling behind after the

session consumers were fixed (ADR 12). Five limits turned up one after another, each hidden by the one before.



\## Findings

| Limit | Evidence | Change |

|---|---|---|

| Per-merchant rate limit (10/s) | 2,383 × 429 at 20/s | Raised for local load tests only |

| Session consumers: 8 slots, 2 s idle | \~2.75 webhooks/s | 32 slots, 0.2 s idle (ADR 12) |

| The mock ParcelGo sent one webhook at a time | One slow answer held up every shipment; \~6 events/s in | Sends for up to 32 shipments at once, in order per shipment |

| The Gateway held a webhook open while the bus was slow | 10 s hangs, carrier timeouts | 4 s budget, then 503 + Retry-After; SDK attempt timeout 2 s |

| The outbox sent one message per round trip | \~5 messages/s, outbox wait rising to \~345 s | One batch (up to 50) per round trip: \~30 ms per batch |

| The emulator stalls for tens of seconds | One outbox send took 27.35 s; the next four took 28–37 ms each | None locally: measure on Azure |



\## Decision

\- Keep all the changes above. Each one fixes behaviour that would hurt with any slow dependency, not only the emulator.

\- Use a short Service Bus attempt timeout in every service (Gateway in code, Tracking in configuration), so one stuck

&#x20; operation is retried within seconds instead of the SDK default of 60 s. Not measured locally: the Gateway showed

&#x20; that a retry does not rescue an emulator stall.

\- Stop optimising against the emulator. Bus capacity is measured on an Azure Service Bus namespace (Phase 6) with the

&#x20; same k6 script and metrics.



\## Consequences

\- Every limit was found by comparing what goes into a hop with what comes out, then opening one trace of a slow

&#x20; message. Latency of delivered messages alone hid a growing queue.

\- The load generator is part of the system under test. A sequential mock capped the input and made the system look

&#x20; slower than it was.

\- A single sequential sender (the outbox publisher) turns any stall in its dependency into a stall of the whole hop.

&#x20; Batching makes it fast between stalls, not immune to them.

