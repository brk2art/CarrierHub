# Ops Assistant evaluation: qwen2.5:7b (tools-v4)

Run 2026-10-27 23:53, 2 cases x 3, fake Public API (World.cs), temperature 0.

```
Model: qwen2.5:7b (tools-v4), each case 3x
Passed: 6/6 (100%) of all runs
Cases: 2 stable pass, 0 flaky, 0 stable fail
Right tool: 6/6 (100%)   Right arguments: 6/6 (100%)   Right answer: 6/6 (100%)
Time: P50 6125 ms, P95 19259 ms   Tokens per question: 2040 in, 93 out
By type:
  lookup     3/3 (100%)
  filter     3/3 (100%)
```

| Case | Type | Verdict | Passed | Median ms | Tools called (last run) | Answer (last run) | Problems (all failing runs) |
|---|---|---|---|---|---|---|---|
| lookup-6 | lookup | STABLE | 3/3 | 19020 | get_shipment_timeline(reference=1011) | Order 1011 is actually ORDER-1011. It's currently in transit and was last updated on October 25, 2026, at 18:00 UTC, at the Odense sorting centre. It was picked up on October 25, 2026, at 12:00 UTC, at the Aarhus terminal. The promised delivery time was October 28, 2026, at 17:00 UTC, and it is not late yet. |  |
| filter-1 | filter | STABLE | 3/3 | 5619 | find_shipments(late=True) | Your late shipments are ORDER-1004 with NordPost and ORDER-1003 with ParcelGo. |  |

## Failing answers
