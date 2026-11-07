# Ops Assistant evaluation: azure-chat (carrier-v2)

Run 2026-11-07 17:02, 6 cases x 5, fake Public API (World.cs), temperature 0.

```
Model: azure-chat (carrier-v2), each case 5x
Passed: 30/30 (100%) of all runs
Cases: 6 stable pass, 0 flaky, 0 stable fail
Right tool: 30/30 (100%)   Right arguments: 30/30 (100%)   Right answer: 30/30 (100%)   Right actions: 30/30 (100%)
Time: P50 2352 ms, P95 2953 ms   Tokens per question: 4632 in, 146 out
By type:
  notfound   5/5 (100%)
  policy     25/25 (100%)
```

| Case | Type | Verdict | Passed | Median ms | Tools called (last run) | Answer (last run) | Problems (all failing runs) |
|---|---|---|---|---|---|---|---|
| notfound-2 | notfound | STABLE | 5/5 | 1265 | get_shipment_timeline(reference=ORDER-9001) | I couldn’t find a shipment with order number ORDER-9001. The merchant’s order numbers look like ORDER-1006. |  |
| pol-2 | policy | STABLE | 5/5 | 2594 | get_shipment_timeline(reference=ORDER-1004)<br>search_carrier_policies(question=compensation for a parcel delivered late or still late while out for delivery; when compensation applies and from when, carrier=NordPost) | ORDER-1004 is late, but NordPost pays late-delivery compensation only if it is delivered more than 24 hours after the promised time; up to 24 hours late gets no compensation. The carrier’s rule is in **(NordPost: Late delivery compensation)**. |  |
| pol-3 | policy | STABLE | 5/5 | 2343 | get_shipment_timeline(reference=ORDER-1003)<br>search_carrier_policies(question=compensation for a parcel delivered 8 hours late, carrier=ParcelGo) | For ORDER-1003, ParcelGo compensates late delivery with a voucher of DKK 25 for future shipments, and it does not refund the shipping fee (ParcelGo: Late delivery compensation). A late-delivery claim must be filed within 7 days after delivery, so this shipment is still within time if handled now. |  |
| pol-4 | policy | STABLE | 5/5 | 2707 | get_shipment_timeline(reference=ORDER-1005)<br>search_carrier_policies(question=compensation for a failed delivery attempt when the customer was not home, and whether it is paid if the parcel is not late, carrier=FastShip) | ORDER-1005 is not late: FastShip says a first delivery attempt before the promised time meets the promise, even if the recipient was not home (FastShip: Failed delivery attempts).   That means no late-delivery compensation applies unless it is actually delivered more than 2 hours after the promised time or remains undelivered past the loss threshold (FastShip: Late delivery compensation). |  |
| pol-5 | policy | STABLE | 5/5 | 2672 | get_shipment_timeline(reference=ORDER-1002)<br>search_carrier_policies(question=What do I need to claim for a damaged parcel, including required evidence and deadlines?, carrier=NordPost) | For ORDER-1002, NordPost’s damaged-parcel rules say you need photos of the damage and you must report it within 7 days after delivery; keep the packaging until NordPost decides the claim (NordPost: Damaged parcels). If you want, I can prepare the claim for this shipment. |  |
| pol-6 | policy | STABLE | 5/5 | 1658 | search_carrier_policies(question=Does FastShip deliver on Sundays?) | I can’t tell from FastShip’s terms whether it delivers on Sundays. The policy section I found only covers promised delivery time, failed delivery attempts, and late-delivery compensation (FastShip: Promised delivery time / Failed delivery attempts / Late delivery compensation). |  |

## Failing answers
