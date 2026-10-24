\# 10. One trace per carrier event, across the bus and the outbox



\## Status

Accepted



\## Context

A carrier event crosses five services, two Service Bus hops and the outbox before a merchant gets a

webhook. Before this change, one status change was split over at least four traces:

\- the poller runs on a timer, so every HTTP call it made started its own trace;

\- Azure SDK tracing is off by default, so Service Bus hops carried no trace context;

\- the outbox publisher runs on its own 1-second timer, long after the request that saved the change;

\- that publisher's empty check created 3,600 traces an hour, and the dashboard dropped real traces to make room.



\## Decision

\- Turn on Azure SDK tracing (`Azure.Experimental.EnableActivitySource`) and collect `Azure.Messaging.ServiceBus.\*`.

&#x20; The SDK puts trace context on each message, and the receiving side continues it as a child span.

\- The poller starts one span per shipment poll (`CarrierHub.Gateway`).

\- Each outbox row stores the W3C `traceparent` of the span that saved it. The publisher continues that trace

&#x20; (`CarrierHub.Tracking.Outbox`) and tags how long the row waited.

\- The publisher's empty check runs inside `SuppressInstrumentationScope`; only real publishes are traced.



\## Consequences

\- One trace, 23 spans across 7 resources, from carrier poll to merchant webhook.

\- Measured: the first event after a start took 2.7 s (first connection to the queue: 570 ms; Notification's first

&#x20; message: 1.6 s), and warm events took 0.18 s. Cold start is a one-time cost per process.

\- Outbox wait is visible in every trace (`carrierhub.outbox.wait\_ms`): 0–1 s, set by the timer, not by the work.

\- The outbox row is a parent, not a link, so a trace can stay open for as long as the outbox is down.

&#x20; Accepted for readability; revisit if traces with long gaps cause trouble in the tracing backend.

\- Still noisy: the Gateway's active-shipments checks (3 traces every 10 s) and polls that find nothing.

&#x20; Sampling is decided before the load test.

