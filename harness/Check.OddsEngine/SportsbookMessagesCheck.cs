using Contracts.Harness;
using OE.Shared.Messages.OutgoingAdapterMessages;

namespace Check.OddsEngine;

/// <summary>
/// What OE.Websocket.Service reads from the OE.Sportsbook.Service goldens, deserialized
/// exactly as its Worker does.
/// </summary>
public class SportsbookMessagesCheck
{
    private static GoldenStore Store => GoldenStore.FromEnvironment()!;

    /// <summary>
    /// OE.Websocket.Service Worker.cs:635 reads OnOddsChange as OnMarketChangeMessage, the
    /// Adapter's input type, not the OnInternalOddsChangeMessage the Sportsbook service sends.
    /// The service copies named fields into its own outgoing types, and none of the allowed
    /// members below is read anywhere in OE.Websocket.Service (checked at odds-engine-2
    /// d705227). If the service starts reading one, take it off this list: it is always default.
    /// </summary>
    [GoldenFact]
    public void Websocket_OnOddsChange_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onoddschange", "OnOddsChange");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnMarketChangeMessage>(golden);
            ContractAssert.NoDefaults(message, allowed:
            [
                // Adapter-input fields the Sportsbook service does not put on the odds change.
                "AwNa", "ChTyId", "ClPr", "HoNa", "Pl",
                "Ma[].ChTyId", "Ma[].MaTeNa",
                "Ma[].Se[].ChTyId", "Ma[].Se[].PrMaId", "Ma[].Se[].PrSeId", "Ma[].Se[].SeTeNa",
                // On the odds change, but the market-change builder never sets them
                // (see Emit.OddsEngine OnOddsChange_FromMarketChange).
                "CuSer", "IsBo", "Ma[].Se[].SeStId", "TiRe", "TiSe",
            ]);
        });
    }

    /// <summary>OE.Websocket.Service Worker.cs:652.</summary>
    [GoldenFact]
    public void Websocket_OnBetStopFixture_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onbetstopfixture", "OnBetStopFixture");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnBetStopFixtureMessage>(golden);
            ContractAssert.NoDefaults(message);
        });
    }

    /// <summary>OE.Websocket.Service Worker.cs:648.</summary>
    [GoldenFact]
    public void Websocket_OnBetStopGlobal_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onbetstopglobal", "OnBetStopGlobal");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnBetStopGlobalMessage>(golden);
            ContractAssert.NoDefaults(message);
        });
    }
}
