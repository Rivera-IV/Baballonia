using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Baballonia.Services;

public interface IEventBus
{
    void Subscribe<T>(Action<T> callback);

    void Unsubscribe<T>(Action<T> callback);

    void Publish<T>(T data);

    bool HasSubscribers<T>();
}

public interface IFacePipelineEventBus : IEventBus
{
}

public interface IEyePipelineEventBus : IEventBus
{
}

public class FacePipelineEventBus : GenericEventBus, IFacePipelineEventBus
{
}

public class EyePipelineEventBus : GenericEventBus, IEyePipelineEventBus
{
}

public class GenericEventBus : IEventBus
{
    // Arrays are immutable snapshots: subscription changes allocate rarely, while the hot publish
    // path reads and iterates without creating a callback array every camera frame.
    private readonly Dictionary<Type, Delegate[]> _subscribers = new();
    private readonly object _lock = new();

    public void Subscribe<T>(Action<T> callback)
    {
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var callbacks))
            {
                _subscribers[typeof(T)] = [callback];
                return;
            }

            var updated = new Delegate[callbacks.Length + 1];
            Array.Copy(callbacks, updated, callbacks.Length);
            updated[^1] = callback;
            _subscribers[typeof(T)] = updated;
        }
    }

    public void Unsubscribe<T>(Action<T> callback)
    {
        lock (_lock)
        {
            if (_subscribers.TryGetValue(typeof(T), out var callbacks))
            {
                var index = Array.IndexOf(callbacks, callback);
                if (index < 0)
                    return;
                if (callbacks.Length == 1)
                {
                    _subscribers.Remove(typeof(T));
                    return;
                }

                var updated = new Delegate[callbacks.Length - 1];
                if (index > 0)
                    Array.Copy(callbacks, 0, updated, 0, index);
                if (index < callbacks.Length - 1)
                    Array.Copy(callbacks, index + 1, updated, index,
                        callbacks.Length - index - 1);
                _subscribers[typeof(T)] = updated;
            }
        }
    }

    public void Publish<T>(T data)
    {
        Delegate[] callbacks;
        lock (_lock)
        {
            if (!_subscribers.TryGetValue(typeof(T), out var list))
                return;
            callbacks = list;
        }

        foreach (var subscriber in callbacks)
        {
            try
            {
                ((Action<T>)subscriber)(data);
            }
            catch (Exception exception)
            {
                Trace.TraceError("Event subscriber for {0} failed: {1}", typeof(T).Name, exception);
            }
        }
    }

    public bool HasSubscribers<T>()
    {
        lock (_lock)
            return _subscribers.TryGetValue(typeof(T), out var list) && list.Length > 0;
    }
}
