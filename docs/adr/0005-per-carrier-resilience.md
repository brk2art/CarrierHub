\# 5. Each carrier fails on its own



Status: Accepted (Phase 2)



\## Context

Carriers fail in different ways: FastShip is fast but flaky, NordPost is slow and sometimes hangs.

With one shared resilience pipeline and carriers polled one after another, a hanging carrier delays

every other carrier's round, and one carrier's failures can open a circuit that all of them share.



\## Decision

\- Each carrier has its own HttpClient with its own resilience pipeline (Polly through

&#x20; Microsoft.Extensions.Http.Resilience), configured under `Carriers:{carrier}:Resilience`:



&#x20; | | FastShip | NordPost |

&#x20; |---|---|---|

&#x20; | Attempt timeout | 2 s | 5 s |

&#x20; | Total timeout | 8 s | 15 s |

&#x20; | Retries | 3, from 200 ms, exponential with jitter | 2, from 1 s, exponential with jitter |

&#x20; | Circuit opens at | 50% failures, at least 10 calls in 30 s | 50% failures, at least 5 calls in 30 s |

&#x20; | Circuit break | 15 s | 30 s |



\- Carriers are polled side by side; shipments of one carrier one after another.

\- An open circuit stops that carrier's round with one log line, instead of failing every shipment.

\- A request limit per carrier (`RequestsPerMinute`), counted in Redis in a fixed one-minute window,

&#x20; so all Gateway instances share it. It counts every attempt, retries included. If Redis is down,

&#x20; it lets requests through (fails open).



\## Test

NordPost was given 20 s latency, 30% errors, duplicates and shuffling. One FastShip and one NordPost

shipment were created.

\- NordPost's circuit opened within the first rounds; its rounds then ended at once.

\- The FastShip shipment (a failed-attempt journey, six events) was delivered on its normal schedule,

&#x20; each event reaching Tracking within one polling round.

\- After NordPost was healed, its circuit went half-open after the 30 s break, then closed. The NordPost

&#x20; shipment was delivered with a complete timeline, with no manual action, because a polling carrier

&#x20; returns its whole history.



\## Consequences

\- Settings are per carrier and live in configuration, so they can change without code changes.

\- The fixed window allows a burst at a window edge (up to twice the limit within a short time).

\- When the limit is reached, the shipments later in the list wait for the next round; with many

&#x20; shipments the same ones would always wait. Not solved yet: rotate the starting point, or poll the

&#x20; longest-waiting shipments first.

\- The rate limit itself hasn't been tested under load yet.

\- With several Gateway instances, each still polls every shipment. They share the limit, but do the

&#x20; same work twice. Phase 3 (messages per shipment) is the place to divide the work.

