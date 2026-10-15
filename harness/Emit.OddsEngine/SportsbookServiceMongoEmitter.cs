using Contracts.Harness;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OE.Shared.Constants;
using OE.Shared.Data.Sportsbook.MongoDB.Collections;
using OE.Shared.Enums;
using OE.Shared.Messages.OutgoingAdapterMessages;
using OE.Shared.Utilities;
using OE.Sportsbook.Service.Repositories;
using OE.Sportsbook.Service.Telemetry;

namespace Emit.OddsEngine;

/// <summary>
/// Goldens for the OE.Sportsbook.Service publish paths that touch MongoDB first: both
/// OnBetSettlement builders and both bet stops. Each runs the real public repository method
/// against a Mongo container and captures what it publishes with FakeMessageBroker (Fakes.cs).
/// </summary>
public class SportsbookServiceMongoEmitter(MongoFixture mongo) : IClassFixture<MongoFixture>
{
    private static GoldenStore Store => GoldenStore.FromEnvironment()!;

    private SportsbookRepository CreateRepository()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Custom:MongoDB-Sportsbook"] = mongo.ConnectionString
            })
            .Build();

        return new SportsbookRepository(
            configuration,
            new FakeMemCache(),
            new Helper(),
            new Instrumentation(),
            NullLogger<SportsbookRepository>.Instance);
    }

    private static void Write(PublishedMessage published, string variant = "")
    {
        Store.Write(new Golden(published.RoutingKey, published.MessageType, ProducerJson.Serialize(published.Payload!), variant));
    }

    /// <summary>
    /// ProcessOnMarketSettlementMessage (SportsbookRepository.cs:142) updates the stored markets
    /// (IsUpsert = false, so only markets already in the catalogue), reads each back, and
    /// publishes one OnBetSettlement per selection of a market it found.
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnBetSettlement_FromMarketSettlement()
    {
        // Arrange
        var input = SamplePopulator.Fill<OnMarketSettlementMessage>();
        foreach (var selection in input.Ma.SelectMany(m => m.Se))
            selection.SeStId = (int)SettlementStatus.Won; // GetBetStatus throws on an undefined value
        foreach (var market in input.Ma)
        {
            var stored = SamplePopulator.Fill<Market>();
            stored.Id = market.Id;
            stored.MaId = market.MaId;
            mongo.Sportsbook.GetCollection<Market>("Market").InsertOne(stored);
        }
        var broker = new FakeMessageBroker();

        // Act
        CreateRepository().ProcessOnMarketSettlementMessage(broker, input, "correlation-1");

        // Assert
        var published = Assert.Single(broker.Published);
        Assert.Equal(("r.oe.bet.settlement", MessageType.ON_BET_SETTLEMENT), (published.RoutingKey, published.MessageType));
        Assert.Empty(DefaultValueScanner.Scan(published.Payload));
        Write(published, "FromMarketSettlement");
    }

    /// <summary>
    /// ProcessOnBetSettlementMessage (SportsbookRepository.cs:194) finds the stored selections
    /// that match each settled template and publishes one OnBetSettlement per match. Same key
    /// and type as the market-settlement path, different source for each field.
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnBetSettlement_FromBetSettlement()
    {
        // Arrange
        var input = SamplePopulator.Fill<OnBetSettlementMessage>();
        var settled = input.Selections.Single();
        var stored = SamplePopulator.Fill<Selection>();
        stored.PrFiId = input.ProviderFixtureId;
        stored.MaTeId = settled.MarketTemplateId;
        stored.SeTeId = settled.SelectionTemplateId;
        stored.SeSp = settled.SelectionSpecifier;
        mongo.Sportsbook.GetCollection<Selection>("Selection").InsertOne(stored);
        var broker = new FakeMessageBroker();

        // Act
        CreateRepository().ProcessOnBetSettlementMessage(broker, input, "correlation-1");

        // Assert
        var published = Assert.Single(broker.Published);
        Assert.Equal(("r.oe.bet.settlement", MessageType.ON_BET_SETTLEMENT), (published.RoutingKey, published.MessageType));
        Assert.Empty(DefaultValueScanner.Scan(published.Payload));
        Write(published, "FromBetSettlement");
    }

    /// <summary>
    /// ProcessOnBetStopFixtureMessage (SportsbookRepository.cs:249) publishes only for a fixture
    /// already in the catalogue, and builds the payload from the stored fixture.
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnBetStopFixture()
    {
        // Arrange
        var input = SamplePopulator.Fill<OnBetStopFixtureMessage>();
        var stored = SamplePopulator.Fill<Fixture>();
        stored.Id = input.Id;
        mongo.Sportsbook.GetCollection<Fixture>("Fixture").InsertOne(stored);
        var broker = new FakeMessageBroker();

        // Act
        CreateRepository().ProcessOnBetStopFixtureMessage(broker, input, "correlation-1");

        // Assert
        var published = Assert.Single(broker.Published);
        Assert.Equal(
            ($"r.oe.sb.engine.{MessageType.ON_BET_STOP_FIXTURE.ToLower()}", MessageType.ON_BET_STOP_FIXTURE),
            (published.RoutingKey, published.MessageType));
        Assert.Empty(DefaultValueScanner.Scan(published.Payload));
        Write(published);
    }

    /// <summary>
    /// ProcessOnBetStopGlobalMessage (SportsbookRepository.cs:229) writes the BettingStatus
    /// document, then publishes when the global bet stop changes.
    /// </summary>
    [GoldenFact]
    [Trait("Category", "Emit")]
    public void OnBetStopGlobal()
    {
        // Arrange: a new repository starts with the bet stop off, so IsBetStop = true is a change.
        var input = SamplePopulator.Fill<OnBetStopGlobalMessage>();
        var broker = new FakeMessageBroker();

        // Act
        CreateRepository().ProcessOnBetStopGlobalMessage(broker, input, "correlation-1");

        // Assert
        var published = Assert.Single(broker.Published);
        Assert.Equal(
            ($"r.oe.sb.engine.{MessageType.ON_BET_STOP_GLOBAL.ToLower()}", MessageType.ON_BET_STOP_GLOBAL),
            (published.RoutingKey, published.MessageType));
        Assert.Empty(DefaultValueScanner.Scan(published.Payload));
        Write(published);
    }
}
