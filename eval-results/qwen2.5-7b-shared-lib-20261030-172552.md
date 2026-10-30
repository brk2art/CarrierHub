# Ops Assistant evaluation: qwen2.5:7b (shared-lib)

Run 2026-10-30 17:25, 1 cases x 1, fake Public API (World.cs), temperature 0.

```
Model: qwen2.5:7b (shared-lib), each case 1x
Passed: 1/1 (100%) of all runs
Cases: 1 stable pass, 0 flaky, 0 stable fail
Right tool: 1/1 (100%)   Right arguments: 1/1 (100%)   Right answer: 1/1 (100%)
Time: P50 15745 ms, P95 15745 ms   Tokens per question: 2084 in, 137 out
By type:
  lookup     1/1 (100%)
```

| Case | Type | Verdict | Passed | Median ms | Tools called (last run) | Answer (last run) | Problems (all failing runs) |
|---|---|---|---|---|---|---|---|
| lookup-1 | lookup | STABLE | 1/1 | 15745 | get_shipment_timeline(reference=ORDER-1001) | ORDER-1001 is currently in transit. It was picked up at the Aarhus terminal on 2026-10-26 08:00 UTC and is now at the Copenhagen hub. The latest update was on 2026-10-26 10:30 UTC. It is not late according to the promised delivery time of 2026-10-28T17:00:00+00:00. |  |

## Failing answers
