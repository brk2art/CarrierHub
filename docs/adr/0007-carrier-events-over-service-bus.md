\# 7. Carrier events travel over Azure Service Bus, one session per shipment



Status: Accepted (Phase 3)



\## Context

Until Phase 2 the Gateway called Tracking over HTTP. The Gateway waited for every write, a Tracking

outage turned into retries in the Gateway, events of the same shipment could race each other (ADR 2's

retry loop), and events for unknown shipments were logged and dropped (ADR 6).



\## Decision

\- The Gateway publishes each carrier event to the `carrier-events` queue; Tracking consumes it.

\- `SessionId` = carrier and tracking number. Service Bus gives a session to one consumer at a time, so a

&#x20; shipment's events are applied one after another; up to 8 shipments are processed in parallel.

\- `MessageId` = carrier, tracking number and carrier event ID, with duplicate detection on: the bus drops

&#x20; a repeat within its window. Redis in the Gateway and the database key in Tracking remain; only the

&#x20; database key is needed for correctness.

\- Tracking completes a message only after the event is saved. Events it can't apply go to the

&#x20; dead-letter queue with a reason (`UnknownShipment`, `InvalidEvent`, `InvalidMessage`), so they're

&#x20; parked, not lost. Any other failure lets the bus deliver the message again, up to 10 times, then

&#x20; dead-letters it. Expired messages are dead-lettered too, not deleted.

\- The rules for applying an event live in one function (`CarrierEvents.ApplyAsync`), used by both the

&#x20; queue consumer and the HTTP endpoint the integration tests call.

\- For ParcelGo webhooks, "200 only once stored" now means "once on the bus", which is durable.

\- Locally, Aspire runs the Service Bus emulator; no Azure subscription is needed until Phase 6.



\## Consequences

\- The Gateway no longer depends on Tracking being up to accept carrier events.

\- The Gateway no longer sees whether an event was new or a duplicate; it counts what it published and

&#x20; what Redis let it skip. Status changes are now logged by Tracking.

\- The emulator differs from Azure: duplicate detection is limited to 5 minutes (Azure allows up to 7 days),

&#x20; it uses a fixed namespace name, and it needs a SQL Server container. The 5-minute window is enough

&#x20; here because the Gateway and the database catch older repeats.

\- Dead-lettered events need a way back: replaying them once the cause is fixed comes in a later step.

\- The Gateway still asks Tracking over HTTP which shipments to poll; a "shipment created" message

&#x20; (from the outbox, next step) can replace that.

