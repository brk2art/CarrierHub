using CarrierHub.Tracking.Domain;
using Xunit;

namespace CarrierHub.Tracking.Tests;

public class ShipmentTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);

    private static Shipment NewShipment(DateTimeOffset? promisedBy = null) =>
        new("merchant-1", "ORDER-1001", "FastShip", "FS123456", promisedBy ?? T0.AddDays(2), createdAt: T0);

    // An event that happened `hours` after T0 and reached us 5 minutes later.
    private static TrackingEvent Event(string id, ShipmentStatus status, double hours) =>
        new(id, status, occurredAt: T0.AddHours(hours), receivedAt: T0.AddHours(hours).AddMinutes(5));

    private static string Ids(Shipment s) => string.Join(",", s.Timeline.Select(e => e.CarrierEventId));

    [Fact]
    public void New_shipment_starts_as_created_with_an_empty_timeline()
    {
        var s = NewShipment();

        Assert.Equal(ShipmentStatus.Created, s.Status);
        Assert.Empty(s.Timeline);
    }

    [Fact]
    public void Events_in_order_move_the_status_forward()
    {
        var s = NewShipment();
        s.AddEvent(Event("e1", ShipmentStatus.PickedUp, 1));
        s.AddEvent(Event("e2", ShipmentStatus.InTransit, 5));
        var outcome = s.AddEvent(Event("e3", ShipmentStatus.OutForDelivery, 24));

        Assert.Equal(ShipmentStatus.OutForDelivery, s.Status);
        Assert.Equal("e1,e2,e3", Ids(s));
        Assert.True(outcome.StatusChanged);
        Assert.Equal(ShipmentStatus.InTransit, outcome.Before);
    }

    [Fact]
    public void A_duplicate_event_is_ignored()
    {
        var s = NewShipment();
        s.AddEvent(Event("e1", ShipmentStatus.PickedUp, 1));
        var outcome = s.AddEvent(Event("e1", ShipmentStatus.PickedUp, 1));

        Assert.Equal(EventResult.Duplicate, outcome.Result);
        Assert.False(outcome.StatusChanged);
        Assert.Single(s.Timeline);
    }

    [Fact]
    public void Delivered_arriving_before_out_for_delivery_still_gives_the_right_timeline()
    {
        var s = NewShipment();
        s.AddEvent(Event("e1", ShipmentStatus.PickedUp, 1));
        s.AddEvent(Event("e3", ShipmentStatus.Delivered, 28));                  // arrives first
        var late = s.AddEvent(Event("e2", ShipmentStatus.OutForDelivery, 24));  // happened earlier, arrives later

        Assert.Equal(ShipmentStatus.Delivered, s.Status);
        Assert.Equal("e1,e2,e3", Ids(s));
        Assert.Equal(EventResult.Added, late.Result);
        Assert.False(late.StatusChanged);   // no "out for delivery" notification after "delivered"
    }

    [Fact]
    public void The_arrival_order_never_changes_the_result()
    {
        TrackingEvent[] events =
        [
            Event("e1", ShipmentStatus.PickedUp, 1),
            Event("e2", ShipmentStatus.InTransit, 5),
            Event("e3", ShipmentStatus.OutForDelivery, 24),
            Event("e4", ShipmentStatus.Delivered, 28),
            Event("e2", ShipmentStatus.InTransit, 5),   // the same event sent twice
        ];

        foreach (var order in Permutations(events))   // all 120 arrival orders
        {
            var s = NewShipment();
            foreach (var e in order)
                s.AddEvent(e);

            Assert.Equal(ShipmentStatus.Delivered, s.Status);
            Assert.Equal("e1,e2,e3,e4", Ids(s));
            Assert.Equal(T0.AddHours(28), s.DeliveredAt);
        }
    }

    [Fact]
    public void A_final_status_is_not_undone_by_a_later_event()
    {
        var s = NewShipment();
        s.AddEvent(Event("e1", ShipmentStatus.Delivered, 28));
        var outcome = s.AddEvent(Event("e2", ShipmentStatus.InTransit, 30));

        Assert.Equal(ShipmentStatus.Delivered, s.Status);
        Assert.False(outcome.StatusChanged);
        Assert.Equal("e1,e2", Ids(s));   // kept, so support can see the carrier's mistake
    }

    [Fact]
    public void A_failed_attempt_can_go_back_out_for_delivery()
    {
        var s = NewShipment();
        s.AddEvent(Event("e1", ShipmentStatus.OutForDelivery, 24));
        s.AddEvent(Event("e2", ShipmentStatus.DeliveryFailed, 27));
        Assert.Equal(ShipmentStatus.DeliveryFailed, s.Status);

        s.AddEvent(Event("e3", ShipmentStatus.OutForDelivery, 48));
        s.AddEvent(Event("e4", ShipmentStatus.Delivered, 50));
        Assert.Equal(ShipmentStatus.Delivered, s.Status);
    }

    [Fact]
    public void Events_with_the_same_time_are_ordered_by_progress()
    {
        var s = NewShipment();
        s.AddEvent(Event("b", ShipmentStatus.DeliveryFailed, 27));
        s.AddEvent(Event("a", ShipmentStatus.OutForDelivery, 27));

        Assert.Equal(ShipmentStatus.DeliveryFailed, s.Status);
        Assert.Equal("a,b", Ids(s));
    }

    [Theory]
    [InlineData(null, 72, true)]    // not delivered, the promised time has passed
    [InlineData(null, 24, false)]   // not delivered, still within the promise
    [InlineData(40, 72, false)]     // delivered before the promise
    [InlineData(50, 72, true)]      // delivered after the promise
    public void Lateness_is_measured_against_the_promised_time(int? deliveredAfterHours, int nowAfterHours, bool expectedLate)
    {
        var s = NewShipment(promisedBy: T0.AddHours(48));
        if (deliveredAfterHours is int h)
            s.AddEvent(Event("d", ShipmentStatus.Delivered, h));

        Assert.Equal(expectedLate, s.IsLate(T0.AddHours(nowAfterHours)));
    }

    [Fact]
    public void A_carrier_event_cannot_set_the_status_to_created() =>
        Assert.Throws<ArgumentException>(() => Event("e1", ShipmentStatus.Created, 1));

    private static IEnumerable<IReadOnlyList<T>> Permutations<T>(IReadOnlyList<T> items)
    {
        if (items.Count <= 1)
        {
            yield return items;
            yield break;
        }

        for (var i = 0; i < items.Count; i++)
        {
            var rest = items.Where((_, j) => j != i).ToList();
            foreach (var p in Permutations(rest))
                yield return [items[i], .. p];
        }
    }
}