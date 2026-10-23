\# 3. Redis dedupe in front of an idempotent receiver



Status: Accepted (Phase 2)



\## Context

Polling APIs return a shipment's whole history on every call. The Gateway polls every 10 seconds, so

without dedupe it re-sends every known event to Tracking each round. Tracking already rejects

duplicates (ADR 2), so this costs work, not correctness.



\## Decision

\- Per shipment, Redis keeps a set of the carrier event IDs already forwarded

&#x20; (`forwarded:{carrier}:{trackingNumber}`, expires after 30 days).

\- Each round checks the whole batch in one call (SMISMEMBER) and sends only what's new.

\- An ID is added only after Tracking confirmed the event, so a crash in between causes a resend, never a loss.

\- If Redis is unavailable, the Gateway sends everything and logs a warning. Tracking stays the

&#x20; source of truth for "seen before"; Redis is only an optimisation in front of it.



\## Measurement

Same workload twice: two FastShip and two NordPost shipments, polled every 10 seconds until delivered.



| | New events | Calls to Tracking | Calls per new event |

|---|---|---|---|

| Without dedupe | 18 | 141 | 7.8 |

| With Redis dedupe | 16 | 16 | 1.0 |



87% fewer calls to Tracking, with identical timelines. A simple model (each round re-sends the history

so far) predicted about 120 wasted calls for the first run; 123 were measured.



\## Consequences

\- Tracking load grows with new events, not with polling frequency.

\- One Redis round trip per shipment per round, plus one write per new event.

\- Open: the second run skipped 108 events where the model expected about 80. Not explained yet;

&#x20; logging rounds per shipment would show why.

