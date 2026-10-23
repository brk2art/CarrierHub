# 2. Shipment status is recalculated from the full timeline

Status: Accepted (Phase 1)

## Context
Carriers send the same event more than once, send events out of order ("Delivered" before
"Out for delivery"), and sometimes send events after a shipment is already delivered.
Merchants must see a correct status and timeline anyway, and must be notified only of real changes.

## Decision
- Every event is stored. The status is recalculated by replaying all events, ordered by when they
  happened at the carrier (`OccurredAt`), with ties broken by progress order.
- Delivered and Returned are final: later events stay in the timeline but don't change the status.
- An event is a duplicate if its carrier event ID was seen before for that shipment. The domain
  ignores it, and the database primary key `(ShipmentId, CarrierEventId)` rejects it if two
  requests race past the domain check.
- `AddEvent` reports whether the status changed; only then will a notification go out (Phase 3).
- Two requests changing the same shipment are caught with optimistic concurrency on PostgreSQL's
  `xmin`; the loser reloads and tries again, up to 5 times.

## Alternatives considered
- Status from the last event received: wrong as soon as events arrive out of order.
- Rejecting events older than the current status: loses data that support needs.
- Event sourcing for the whole platform: more cost than value here; the per-shipment timeline
  gives the useful part.

## Consequences
- The arrival order cannot change the result: a unit test replays all 120 arrival orders of five
  events (one duplicate) and gets the same status and timeline every time.
- A late "Out for delivery" after "Delivered" doesn't trigger a notification.
- Each new event replays the shipment's timeline; cheap, because a shipment has tens of events.
- The carrier's clock is trusted for ordering.
- Under bursts, requests for the same shipment retry against each other. Phase 3 removes the race
  with Service Bus sessions: one consumer at a time per shipment.
- A carrier's wrong event after delivery stays visible in the timeline for support, but has no effect.