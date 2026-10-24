\# 8. Status changes are announced through a transactional outbox



Status: Accepted (Phase 3)



\## Context

A status change must be saved in PostgreSQL and announced on Service Bus. There is no transaction across

both: saving first and then publishing loses the announcement if the process stops in between; publishing

first announces a change that may never be saved.



\## Decision

\- The domain raises `ShipmentCreated` and `ShipmentStatusChanged` (only for real changes; duplicates and

&#x20; late events that change nothing raise nothing).

\- `TrackingDbContext.SaveChangesAsync` turns them into `OutboxMessages` rows in the same transaction as the

&#x20; change. There's no other way to save, so no code path can forget.

\- `OutboxPublisher` runs every second: it locks a batch of unpublished rows with `FOR UPDATE SKIP LOCKED`

&#x20; (several Tracking instances take different rows), publishes them to the `shipment-events` topic and marks

&#x20; them published. It stops at the first failure in a batch, so messages never overtake each other.

\- `MessageId` is the outbox row ID (the topic drops a re-publish within its window); `SessionId` is the

&#x20; shipment, so each subscriber receives a shipment's messages in order.

\- Delivery is at least once. Subscribers must handle repeats.



\## Test

\- Integration test: a change writes exactly one message with it; a duplicate and a non-changing event write

&#x20; nothing; payloads carry the merchant and the new status.

\- Normal flow: one shipment produced five messages (created, four changes), each published 0.8–1.7 s after

&#x20; it was saved.

\- Outage: with the emulator stopped, a status change was saved at once. Its message waited 68 s (5 attempts,

&#x20; last error "entity not found" while the restarted emulator recreated its topics) and was published once

&#x20; the bus was back, before the next, newer message.



\## Consequences

\- A status change can't be lost or invented between the database and the bus.

\- Announcements are delayed by up to about a second in normal operation.

\- Published rows stay in the table; a clean-up job (delete rows published more than N days ago) is still to do.

\- jsonb stores parsed JSON: payloads come back with different spacing and key order. Readers must compare

&#x20; values, not text.

\- The local emulator drops its stored messages when it restarts; Azure Service Bus doesn't. Local tests of

&#x20; "the subscriber was down" must stop the subscriber, not the emulator.

