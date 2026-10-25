\# 11. Polling within a carrier's request budget



\## Status

Accepted



\## Context

Polling carriers have a request budget: FastShip 120 per minute, NordPost 30. Every active shipment costs one

request per round, so a carrier can be polled every 10 s only up to RequestsPerMinute × 10 / 60 active

shipments: 20 for FastShip, 5 for NordPost. A load of 1 shipment/s (about 60 per carrier over 3 minutes)

is well past that for NordPost.



Two questions: in which order should a round spend the budget, and how do we see that we are over it?



\## Measurements

NordPost, k6 at 1 shipment/s for 3 minutes, each run from an empty database. The mock carrier's journey starts

when the shipment is created, as with a real carrier. (Earlier runs started it at the first poll, which hid

the wait before that poll from the event age and made the results look the opposite way round.)



| | Tracking's order | Fair (least recently polled first, a fair share of the budget per round) |

|---|---|---|

| Event age, peak P50 / P99 | 60 s / 180 s | 120 s / 180 s |

| Poll gap, peak P50 / P99 | 60 s / 90 s | 180 s / 180 s |

| Created → delivered seen, peak P50 / P99 | 180 s / 300 s | 240 s / 300 s |

| Polls used / deferred | 203 / 960 | 195 / 970 |



Journeys take 120 s, or 210 s with a failed delivery attempt.



\## Decision

\- Rounds use Tracking's order by default. Fair mode stays available as `Polling:Fair`.

\- `carrierhub.gateway.polls{outcome=deferred}` above zero for a sustained period means more active shipments than

&#x20; the budget allows; it is the capacity alarm. `gateway.event.age`, `gateway.poll.gap` and

&#x20; `shipment.time\_to\_delivered` show the effect.



\## Consequences

\- At this overload, fair sharing slowed the typical shipment (median a minute later) and did not shorten the

&#x20; worst case. With a budget too small for everyone, finishing some shipments beats delaying all of them.

\- Under long overload with an unordered list, Tracking's order could starve a shipment. Not seen in the runs;

&#x20; ordering the active-shipments query by creation time makes it first in, first out.

\- The polling state is per Gateway instance. Two instances would each poll every shipment; scaling the Gateway

&#x20; needs a shared schedule (not built).

\- The lasting fix is fewer wasted polls: about 2 per journey would be enough, 3 were used. Deciding when a

&#x20; shipment is likely to have changed is a prediction problem (Chapters 10–12).

