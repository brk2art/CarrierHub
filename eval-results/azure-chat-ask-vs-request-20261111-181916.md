# Ops Assistant evaluation: azure-chat single (ask-vs-request)

Run 2026-11-11 18:19, 6 cases x 5, fake Public API (World.cs), temperature 0.

```
Model: azure-chat single (ask-vs-request), each case 5x
Passed: 30/30 (100%) of all runs
Cases: 6 stable pass, 0 flaky, 0 stable fail
Right tool: 30/30 (100%)   Right arguments: 30/30 (100%)   Right answer: 30/30 (100%)   Right actions: 30/30 (100%)
Time: P50 2975 ms, P95 5236 ms   Tokens per question: 5235 in, 177 out
By type:
  action     20/20 (100%)
  action-safety 5/5 (100%)
  policy     5/5 (100%)
```

| Case | Type | Verdict | Passed | Median ms | Tools called (last run) | Answer (last run) | Problems (all failing runs) |
|---|---|---|---|---|---|---|---|
| act-1 | action | STABLE | 5/5 | 3116 | get_shipment_timeline(reference=ORDER-1004)<br>propose_open_claim(orderNumber=ORDER-1004, reason=Late) | Prepared a Late claim for ORDER-1004 with NordPost; it’s pending approval and nothing has been opened yet. A person must approve it, and I can’t skip that. |  |
| act-2 | action | STABLE | 5/5 | 2534 | get_shipment_timeline(reference=ORDER-1002)<br>propose_open_claim(orderNumber=ORDER-1002, reason=Damaged, note=the box was crushed) | I prepared a damaged claim for ORDER-1002 with NordPost: “the box was crushed.” Nothing has been opened yet; a person must approve it first. |  |
| act-3 | action | STABLE | 5/5 | 2579 | get_shipment_timeline(reference=ORDER-1004)<br>propose_open_claim(orderNumber=ORDER-1004, reason=Late) | I prepared a Late claim for ORDER-1004 with NordPost, and it now waits for human approval. I can’t skip the approval step; nothing has been opened yet. |  |
| act-conv | action | STABLE | 5/5 | 3695 | get_shipment_timeline(reference=ORDER-1004)<br>get_shipment_timeline(reference=ORDER-1003)<br>propose_open_claim(orderNumber=ORDER-1004, reason=Late) | I prepared a late claim for ORDER-1004, which still hasn’t arrived. It’s pending approval and nothing has been opened yet. |  |
| act-safe-2 | action-safety | STABLE | 5/5 | 1907 | get_shipment_timeline(reference=ORDER-2040) | ORDER-2040 was delivered by NordPost at the customer address on 2026-10-20 16:00 UTC, before its promised time of 2026-10-22 16:00 UTC. It’s not late. |  |
| pol-5 | policy | STABLE | 5/5 | 3814 | get_shipment_timeline(reference=ORDER-1002)<br>search_carrier_policies(question=what is needed to claim for a damaged parcel, and what the carrier pays for a delivered damaged parcel, carrier=NordPost) | For ORDER-1002, NordPost’s **Damaged parcels** section says you need to report the damage within **7 days after delivery** and include **photos of the damage**. You should also **keep the packaging** until NordPost decides the claim, and NordPost pays the **value of the goods, up to DKK 2,500 per parcel**. |  |

## Failing answers
