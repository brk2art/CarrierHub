\# 4. Carrier times without an offset are read in the carrier's time zone



Status: Accepted (Phase 2)



\## Context

FastShip sends times with an offset (`+02:00`), which convert to UTC exactly. NordPost sends date and

time as separate fields in Danish local time, with no offset. Such a time only has a meaning together

with a time zone, and twice a year it's ambiguous:

\- in autumn (25 October 2026) the hour 02:00–03:00 happens twice;

\- in spring (28 March 2027) the hour 02:00–03:00 never happens.



\## Decision

\- Every carrier without offsets has its time zone configured in its adapter (NordPost: Europe/Copenhagen).

\- An ambiguous time is read as its first occurrence (summer time).

\- A time that doesn't exist is moved forward by the gap (02:30 becomes 03:30).

\- Everything is stored as UTC.



\## Consequences

\- Contract tests cover all four cases: summer, winter, the repeated hour and the missing hour.

\- For events in the repeated autumn hour, the timeline can be off by up to one hour. Tracking orders

&#x20; by time, so two NordPost events in that hour could appear in the wrong order. Accepted: it affects one

&#x20; hour a year, and the status rules (final statuses stick) keep the status right.

\- If NordPost ever adds offsets, the adapter should prefer them.

