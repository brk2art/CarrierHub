\# 9. Merchants are notified by signed webhooks, in order per shipment, retried and dead-lettered



Status: Accepted (Phase 3)



\## Context

Merchants want to know when a shipment's status changes, without polling. CarrierHub now has the same

problem ParcelGo had: the receiver can be slow, down, or wrong, and it must be able to trust the sender.



\## Decision

\- A Notification service owns merchants' webhooks and a delivery log, in its own database.

\- It reads the `notifications` subscription of `shipment-events`, one session per shipment, so a merchant

&#x20; receives a shipment's changes in order, and a failing message holds up only its own shipment.

\- Each merchant gets a random secret when registering, shown once. Every delivery is signed with it:

&#x20; HMAC-SHA256 over "{timestamp}.{body}" in `CarrierHub-Signature: t=...,v1=...`, the same scheme the

&#x20; Gateway checks for ParcelGo. `CarrierHub-Event-Id` carries the outbox message ID.

\- A message is delivered successfully at most once: the delivery log has a unique index on successful

&#x20; deliveries per message, checked before sending.

\- A failed attempt is retried twice within seconds; then the bus delivers it again, up to 5 times; then it's

&#x20; parked in the dead-letter queue. `POST /admin/dead-letters/replay` delivers parked messages in order and

&#x20; stops at the first failure.

\- No circuit breaker on merchant webhooks: one breaker shared by all merchants would let one broken

&#x20; endpoint stop deliveries to everyone.

\- Webhook URLs must be https. Merchants manage theirs through the Public API (`/v1/webhook`), with the

&#x20; merchant taken from the token.



\## Test

\- Normal flow: four status changes reached the mock merchant in order, signed, 0.4–1.0 s after each change.

\- Merchant down for a whole journey: each of four messages was tried 5 times (all 500) and dead-lettered.

&#x20; After the merchant was fixed, replay delivered all four, in order, about 2.5 minutes late.

\- Unit tests: no webhook means nothing sent; the signature can be checked by the receiver; a repeated

&#x20; message isn't sent twice; failures are recorded. The signature matches the shared test vector.



\## Consequences

\- Dead-lettering trades ordering for progress. If a merchant recovers between two changes, the first

&#x20; (parked) can arrive after the second (delivered live). The payload's `previousStatus` and `changedAt`

&#x20; let merchants detect it. Holding a shipment's later messages while an earlier one is parked would keep

&#x20; the order, at real cost in complexity; not done.

\- Retries are fast (seconds) so a test finishes in minutes. Production would back off over minutes or

&#x20; hours, with scheduled messages instead of immediate redelivery.

\- Secrets are stored as they are. Production: encrypt them at rest (Data Protection or Key Vault), and

&#x20; support two secrets during a rotation.

\- Merchant URLs are fetched by our server, so they're an SSRF risk: production must block private and

&#x20; internal address ranges.

\- Replay is an internal, unauthenticated admin endpoint for now; it belongs behind operator authentication.

