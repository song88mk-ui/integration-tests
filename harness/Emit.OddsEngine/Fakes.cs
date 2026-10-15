using OE.Shared.Models;
using OE.Shared.Utilities;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace Emit.OddsEngine;

// Stand-ins for OE.Shared.Test's fakes of the same names. That test project ships in no image,
// so the emitters keep their own copies, written against the image's own IMessageBroker and
// IMemCache. If either interface changes in a deployed image, this file stops compiling, which
// is the signal to update it.

public sealed record PublishedMessage(object? Payload, string MessageType, string RoutingKey, string CorrelationId);

/// <summary>Records every Publish / Publish&lt;T&gt;. Everything else is a no-op.</summary>
public sealed class FakeMessageBroker : IMessageBroker
{
    public List<PublishedMessage> Published { get; } = [];

    public event EventHandler<BasicDeliverEventArgs>? MessageReceived;

    public void Connect() { }
    public void ConnectWithManualAck() { }
    public void ExternalConnect() { }
    public void ExternalConnect(MessageBrokerModel model) { }
    public void Acknowledge(BasicDeliverEventArgs e) { }
    public void NAcknowledge(BasicDeliverEventArgs e) { }
    public void ExternalPublish<T>(T payload, string messageType, string routingKey, string correlationId) { }
    public void ExternalPublish(string payload, string messageType, string routingKey, string correlationId) { }

    public void Publish<T>(T payload, string messageType, string routingKey, string correlationId)
        => Published.Add(new PublishedMessage(payload, messageType, routingKey, correlationId));

    public void Publish(string payload, string messageType, string routingKey, string correlationId)
        => Published.Add(new PublishedMessage(payload, messageType, routingKey, correlationId));

    public void CPublish<T>(T payload, string messageType, string routingKey, string correlationId) { }
    public void CPublish(string payload, string messageType, string routingKey, string correlationId) { }

    public IModel BindQueue(string queueName, Dictionary<string, object> options) => throw new NotSupportedException();

    public void Dispose() => MessageReceived = null;
}

/// <summary>A Dictionary in place of the real static MemoryCache.Default. Nothing expires.</summary>
public sealed class FakeMemCache : IMemCache
{
    private readonly Dictionary<string, object> _store = [];

    public void AddOrUpdateCache<T>(string key, T cacheObject, int cacheTimeout) => _store[key] = cacheObject!;

    public void AddOrUpdateCache<T>(string key, T cacheObject, DateTimeOffset date) => _store[key] = cacheObject!;

    public T GetCache<T>(string key, Func<T> repositoryCall, int expiration) where T : class
    {
        if (_store.TryGetValue(key, out var cached))
            return (T)cached;

        var result = repositoryCall();
        if (result != null)
            AddOrUpdateCache(key, result, expiration);
        return result!;
    }

    public T? GetCache<T>(string key) where T : class =>
        _store.TryGetValue(key, out var cached) ? (T)cached : null;

    public T? GetCacheData<T>(string key) =>
        _store.TryGetValue(key, out var cached) ? (T)cached : default;

    public KeyValuePair<bool, long> GetCache(string key) =>
        _store.TryGetValue(key, out var cached) ? (KeyValuePair<bool, long>)cached : new KeyValuePair<bool, long>(false, 0);

    public void DeleteCache(string key) => _store.Remove(key);

    public bool Contains(string key) => _store.ContainsKey(key);

    public List<string> GetExistingKeys(string name) => _store.Keys.Where(k => k.Contains(name)).ToList();

    public void DeleteCacheByPrefix(string prefix)
    {
        foreach (var key in _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList())
            _store.Remove(key);
    }
}
