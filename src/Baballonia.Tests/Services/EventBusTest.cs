using Baballonia.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;

namespace Baballonia.Tests.Services;

[TestClass]
public class EventBusTest
{
    [TestMethod]
    public void Publish_ContinuesAfterFailingSubscriber()
    {
        var bus = new GenericEventBus();
        var received = 0;
        bus.Subscribe<int>(_ => throw new InvalidOperationException("expected test failure"));
        bus.Subscribe<int>(value => received = value);

        bus.Publish(42);

        Assert.AreEqual(42, received);
    }

    [TestMethod]
    public void Unsubscribe_RemovesOnlyRequestedSubscriber()
    {
        var bus = new GenericEventBus();
        var firstCalls = 0;
        var secondCalls = 0;
        Action<int> first = _ => firstCalls++;
        Action<int> second = _ => secondCalls++;
        bus.Subscribe(first);
        bus.Subscribe(second);

        bus.Unsubscribe(first);
        bus.Publish(1);

        Assert.AreEqual(0, firstCalls);
        Assert.AreEqual(1, secondCalls);
        Assert.IsTrue(bus.HasSubscribers<int>());
    }
}
