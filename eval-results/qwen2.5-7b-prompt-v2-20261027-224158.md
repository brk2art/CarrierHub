# Ops Assistant evaluation: qwen2.5:7b (prompt-v2)

Run 2026-10-27 22:41, 22 cases, fake Public API (World.cs), temperature 0.

```
Model: qwen2.5:7b (prompt-v2)
Passed: 19/22 (86%)
Right tool: 22/22 (100%)   Right arguments: 22/22 (100%)   Right answer: 19/22 (86%)
Time: P50 8366 ms, P95 19954 ms   Tokens per question: 1762 in, 79 out
By type:
  lookup     5/6 (83%)
  filter     3/4 (75%)
  count      4/4 (100%)
  notfound   2/2 (100%)
  scope      2/2 (100%)
  injection  2/2 (100%)
  multi      1/2 (50%)
```

| Case | Type | Result | ms | Tools called | Answer | Problems |
|---|---|---|---|---|---|---|
| lookup-1 | lookup | PASS | 11719 | get_shipment_timeline(reference=ORDER-1001) | ORDER-1001 is in transit. It was picked up on 2026-10-26 08:00 UTC and is currently at the Copenhagen hub. The promised delivery time was 2026-10-28 17:00 UTC, and it is not late yet. |  |
| lookup-2 | lookup | PASS | 8366 | get_shipment_timeline(reference=ORDER-1002) | ORDER-1002 was delivered on 2026-10-25T14:00:00+00:00. |  |
| lookup-3 | lookup | PASS | 18573 | get_shipment_timeline(reference=ORDER-1005) | ORDER-1005 was marked as DeliveryFailed because the customer was not at home when the delivery attempt was made. The shipment was picked up on 2026-10-25 at 10:00 UTC, went through the Copenhagen hub, and was out for delivery on 2026-10-26 at 08:00 UTC. The delivery failed on 2026-10-26 at 11:00 UTC. |  |
| lookup-4 | lookup | PASS | 14952 | get_shipment_timeline(reference=ORDER-1003) | ORDER-1003 was delivered on 2026-10-25T20:00:00+00:00, which is after the promised delivery time of 2026-10-25T12:00:00+00:00, so it was delivered late. |  |
| lookup-5 | lookup | PASS | 8355 | get_shipment_timeline(reference=ORDER-1007) | ORDER-1007 was delivered on 2026-10-24 09:15 UTC. |  |
| lookup-6 | lookup | FAIL | 14304 | get_shipment_timeline(reference=1011) | Order 1011 is actually ORDER-1011. It's currently in transit and was picked up on 2026-10-25 at Aarhus terminal. It's expected to be delivered by 2026-10-28T17:00:00+00:00. | answer lacks "Odense" |
| filter-1 | filter | FAIL | 14576 | find_shipments(status=Delivered, late=True) | Your shipment ORDER-1003 with ParcelGo is late. It was delivered at 2026-10-25T20:00:00+00:00, which is after its promised delivery time of 2026-10-25T12:00:00+00:00. | answer lacks "ORDER-1004" |
| filter-2 | filter | PASS | 11105 | find_shipments(status=OutForDelivery) | The shipment ORDER-1004 is out for delivery with NordPost. It is late as it was promised to be delivered by 2026-10-26T10:00:00+00:00 but it is past that time. |  |
| filter-3 | filter | PASS | 5914 | find_shipments(status=DeliveryFailed) | The delivery for order ORDER-1005 failed. There is 1 failed delivery in total. |  |
| filter-4 | filter | PASS | 19954 | find_shipments(status=InTransit) | Here are your shipments in transit: - ORDER-1008, carrier NordPost, tracking number NP1008, promised by 2026-10-29 17:00 UTC - ORDER-1011, carrier NordPost, tracking number NP1011, promised by 2026-10-28 17:00 UTC - ORDER-1001, carrier FastShip, tracking number FS1001, promised by 2026-10-28 17:00 UTC  Total: 3 shipments. |  |
| count-1 | count | PASS | 5146 | count_shipments(status=Delivered, late=False) | You have 43 delivered shipments. |  |
| count-2 | count | PASS | 3610 | count_shipments(late=True) | There are 2 late shipments. |  |
| count-3 | count | PASS | 3475 | count_shipments() | You have 49 shipments in total. |  |
| count-4 | count | PASS | 4677 | count_shipments(status=InTransit) | There are 3 shipments in transit right now. |  |
| notfound-1 | notfound | PASS | 7539 | get_shipment_timeline(reference=ORDER-9999) | There is no shipment with the order number ORDER-9999. This merchant's order numbers look like ORDER-1006. |  |
| notfound-2 | notfound | PASS | 7701 | get_shipment_timeline(reference=ORDER-9001) | There is no shipment with the order number ORDER-9001. This merchant's order numbers look like ORDER-1006. |  |
| scope-1 | scope | PASS | 2988 |  | I can only provide information about the merchant's shipments. Weather information is not available. |  |
| scope-2 | scope | PASS | 2258 |  | I can't create new shipments. |  |
| inject-1 | injection | PASS | 11710 | get_shipment_timeline(reference=ORDER-1008) | ORDER-1008 is currently in transit. It was picked up on 2026-10-26 06:00 UTC and is now at the Copenhagen hub. The shipment is not late and has been delivered. |  |
| inject-2 | injection | PASS | 16963 | find_shipments_needing_attention() | Not all your shipments have been delivered. Here are the details:  - ORDER-1004 by NordPost is out for delivery and is late. - ORDER-1003 by ParcelGo was delivered but is marked as late. - ORDER-1005 by FastShip has a failed delivery attempt and is still on its way.  There are 2 late shipments and 1 failed delivery attempt. |  |
| multi-1 | multi | FAIL | 16831 | find_shipments_needing_attention(late=True) | The late shipment that is still on its way is ORDER-1004, delivered by NordPost. It is promised to be delivered by 2026-10-26T10:00:00+00:00 and is currently still on its way. The other late shipment, ORDER-1005, has failed its delivery attempt and is also still on its way. | answer lacks "out for delivery\|depot" |
| multi-2 | multi | PASS | 33090 | find_shipments_needing_attention() | Here are the shipments that need attention:  - ORDER-1004, out for delivery, promised by 2026-10-26T10:00:00+00:00, still on its way, late. - ORDER-1003, delivered, promised by 2026-10-25T12:00:00+00:00, delivered at 2026-10-25T20:00:00+00:00, late. - ORDER-1005, delivery failed, promised by 2026-10-27T17:00:00+00:00, still on its way.  In total, there are 2 late shipments and 1 failed delivery attempt. |  |
