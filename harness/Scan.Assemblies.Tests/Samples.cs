namespace Scan.Assemblies.Tests.Samples;

// Publish sites shaped like the ones in the service repos. The tests scan this assembly's own IL.
// Every key starts with "r.sample." so other code in the assembly cannot interfere.

public interface ISampleBroker
{
    event EventHandler<EventArgs>? MessageReceived;
    void Publish<T>(T payload, string messageType, string routingKey, string correlationId);
    void CPublish(string payload, string messageType, string routingKey, string correlationId);
    void ExternalPublish(string payload, string messageType, string routingKey, string correlationId);
}

public sealed class GatewayRequest
{
    public string? RoutingKey { get; set; }
}

public static class MessageType
{
    public const string ON_ODDS_CHANGE = "OnOddsChange";
}

public sealed class Producer(ISampleBroker broker)
{
    private const string ErrorKey = "r.sample.error.const";

    public void Literal() => broker.Publish(1, "T", "r.sample.literal", "c");

    public void CPublishLiteral() => broker.CPublish("p", "T", "r.sample.cpublish", "c");

    public void ConstantInterpolated()
        => broker.Publish(1, "T", $"r.sample.engine.{MessageType.ON_ODDS_CHANGE.ToLower()}", "c");

    public void ConstField() => broker.Publish(1, "T", ErrorKey, "c");

    public void UnknownSuffix(string clientCode) => broker.Publish(1, "T", "r.sample.betstatus." + clientCode, "c");

    public void Formatted(int shard) => broker.Publish(1, "T", string.Format("r.sample.format.{0:D2}.end", shard), "c");

    public void Loop()
    {
        for (var i = 0; i < 10; i++)
            broker.Publish(1, "T", $"r.sample.loop.{i:D2}", "c");
    }

    public void LiteralLocal()
    {
        var instance = "10";
        broker.Publish(1, "T", $"r.sample.local.{instance}", "c");
    }

    public void TwoBranches(bool flag)
    {
        string messageType;
        if (flag)
            messageType = "First";
        else
            messageType = "Second";
        broker.Publish(1, "T", $"r.sample.branch.{messageType.ToLower()}", "c");
    }

    public async Task Async(Task before)
    {
        var messageType = "OnAsync";
        await before;
        broker.Publish(1, "T", $"r.sample.async.{messageType.ToLower()}", "c");
    }

    public GatewayRequest Gateway() => new() { RoutingKey = "r.sample.gateway" };

    public void External() => broker.ExternalPublish("p", "T", "r.sample.external", "c");

    public bool Compare(string key) => key == "r.sample.compare" || key.StartsWith("r.sample.startswith");

    public void Subscribe() => broker.MessageReceived += (_, _) => { };
}
