using System.Reflection;
using Contracts.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OE.Shared.Constants;
using OE.Shared.Messages.OutgoingAdapterMessages;
using OE.Shared.Messages.OutgoingSportsbookMessages;
using OE.Shared.Utilities;
using OE.Sportsbook.Service.Repositories;
using OE.Sportsbook.Service.Telemetry;

namespace Emit.OddsEngine;

/// <summary>
/// Goldens for what OE.Sportsbook.Service publishes on e.central. Only the paths that run
/// without MongoDB are covered: the odds-change builder and the market-cancellation publish.
/// Settlement and both bet stops read or write Mongo before publishing, so they need a Mongo
/// container (not approved yet).
/// </summary>
public class SportsbookServiceEmitter
{
    private static SportsbookRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never connected to: MongoClient and GetCollection are lazy, and the paths
                // below make no Mongo call.
                ["Custom:MongoDB-Sportsbook"] = "mongodb://localhost:27017"
            })
            .Build();

        return new SportsbookRepository(
            configuration,
            new FakeMemCache(),
            new Helper(),
            new Instrumentation(),
            NullLogger<SportsbookRepository>.Instance);
    }

    private static GoldenStore Store => GoldenStore.FromEnvironment()!;

    /// <summary>
    /// ProcessOnMarketChangeMessage builds the payload with PrepareInternalOddsChangeMessage,
    /// bulk-writes Mongo, then publishes the payload unchanged. The builder is internal (visible
    /// to OE.Sportsbook.Service.Tests only), so it is called by reflection.
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnOddsChange_FromMarketChange()
    {
        // Arrange
        var input = SamplePopulator.Fill<OnMarketChangeMessage>();
        // Market template ids are numeric strings; the payload's market SoOr is int.Parse(MaTeId)
        // (GetSortOrder, SportsbookRepository.cs:910).
        foreach (var market in input.Ma)
            market.MaTeId = "16";
        var builder = typeof(SportsbookRepository).GetMethod(
            "PrepareInternalOddsChangeMessage",
            BindingFlags.Instance | BindingFlags.NonPublic,
            [typeof(OnMarketChangeMessage), typeof(string)])
            ?? throw new MissingMethodException(nameof(SportsbookRepository), "PrepareInternalOddsChangeMessage");

        // Act
        var payload = (OnInternalOddsChangeMessage)builder.Invoke(CreateRepository(), [input, "correlation-1"])!;

        // Assert: every field the producer fills is in the golden. These five are on
        // OnInternalOddsChangeMessage but the market-change builder never sets them, so they are
        // always default on this path. A consumer that reads one of them gets 0 / null.
        Assert.Equal(
            [
                "CuSer",            // commented out in the builder (SportsbookRepository.cs:764)
                "IsBo",             // not mapped; the Mongo write is a TODO (:392)
                "Ma[].Se[].SeStId", // not mapped here; only the fixture-change builder sets it (:818)
                "TiRe",             // base-class timestamps, not copied onto the odds change
                "TiSe",
            ],
            DefaultValueScanner.Scan(payload));
        Store.Write(new Golden(
            $"r.oe.sb.engine.{MessageType.ON_ODDS_CHANGE.ToLower()}",
            MessageType.ON_ODDS_CHANGE,
            ProducerJson.Serialize(payload)));
    }

    /// <summary>
    /// Captured from the real publish. OE.Sportsbook.Service's Worker has no ON_MARKET_CANCEL
    /// case, so nothing calls this method today (pinned by its own
    /// WorkerTests.MessageReceived_OnMarketCancel_FallsThroughWithNoDispatch). The golden is the
    /// contract the consumers will see if it is ever wired (deferred in GM-959, tracked in GM-962).
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnBetCancellation_AndOnMarketCancel_FromMarketCancellation()
    {
        // Arrange
        var input = SamplePopulator.Fill<OnMarketCancellationMessage>();
        var broker = new FakeMessageBroker();

        // Act
        CreateRepository().ProcessOnMarketCancellationMessage(broker, input, "correlation-1");

        // Assert
        Assert.Equal(
            [
                ("r.client.topmarket.settlement", MessageType.ON_MARKET_CANCEL),
                ("r.oe.bet.settlement", MessageType.ON_BET_CANCELLATION),
            ],
            broker.Published.Select(p => (p.RoutingKey, p.MessageType)));

        foreach (var published in broker.Published)
        {
            // SettlementScore is hard-coded to string.Empty ("TODO: check with product team",
            // SportsbookRepository.cs:310), so it is default on the wire by design.
            Assert.Equal(["SettlementScore"], DefaultValueScanner.Scan(published.Payload));
            Store.Write(new Golden(published.RoutingKey, published.MessageType, ProducerJson.Serialize(published.Payload!)));
        }
    }
}
