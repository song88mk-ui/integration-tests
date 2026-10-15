using Contracts.Harness;
using Backoffice.Shared.IncomingSbEngineMessages;

namespace Check.Backoffice;

/// <summary>
/// What Backoffice.WebSocket.Service reads from the OE.Sportsbook.Service goldens,
/// deserialized exactly as its Worker does.
/// </summary>
public class OddsEngineMessagesCheck
{
    private static GoldenStore Store => GoldenStore.FromEnvironment()!;

    /// <summary>
    /// Backoffice.WebSocket.Service Worker.cs:149. Allowed members are either never read by
    /// Backoffice.WebSocket.Service or Backoffice.Shared, or read but never shown (checked at
    /// backoffice-final origin/main, 2026-10-14).
    /// </summary>
    [GoldenFact]
    public void WebSocket_OnOddsChange_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onoddschange", "OnOddsChange");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<IncomingOddsChangeMessage>(golden);
            ContractAssert.NoDefaults(message, allowed:
            [
                // OE v1 fields with no OE2 equivalent, never read.
                "AwayBetradarId", "HomeBetradarId", "LeagueBetradarId", "CurrentServer", "PeriodScores",
                "FixtureTranslations", "IsInsert", "Markets[].IsInsert", "Markets[].Selections[].IsInsert",
                "Markets[].Selections[].ClientOdds",
                // Mapped to IsBo, which the market-change builder never sets; never read.
                "IsBoosted",
                // OE2 sends CoId. Forwarded to the BO UI (SportsbookRepository.cs:118,142) for
                // tracing only.
                "CorrelationId",
                // OE2's odds change carries no PrSeId. Forwarded as PrSeId and stored by the
                // Monitoring page (Monitoring.razor.cs:460), but never displayed.
                "Markets[].Selections[].ProviderSelectionId",
                // BO-124: OE2's CuPe maps to CurrentPeriodId; this string is never filled. Checked
                // on its own below, so the rest of this message keeps gating.
                "CurrentPeriod",
            ]);
        });
    }

    /// <summary>
    /// Backoffice.WebSocket.Service forwards CurrentPeriod as CuPe to the Monitoring page
    /// (SportsbookRepository.cs:273,386), which displays it (Monitoring.razor:83).
    /// </summary>
    [KnownDefectFact("BO-124")]
    public void WebSocket_OnOddsChange_ReadsCurrentPeriod()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onoddschange", "OnOddsChange");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<IncomingOddsChangeMessage>(golden);
            Assert.False(string.IsNullOrEmpty(message.CurrentPeriod));
        });
    }

    /// <summary>Backoffice.WebSocket.Service Worker.cs:158.</summary>
    [GoldenFact]
    public void WebSocket_OnBetStopFixture_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onbetstopfixture", "OnBetStopFixture");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<IncomingBetStopFixtureMessage>(golden);
            ContractAssert.NoDefaults(message, allowed:
            [
                // Documented as absent from the OEv2 wire (IncomingBetStopFixtureMessage.cs:9).
                "MarketTemplateGroups",
            ]);
        });
    }

    /// <summary>Backoffice.WebSocket.Service Worker.cs:162.</summary>
    [GoldenFact]
    public void WebSocket_OnBetStopGlobal_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onbetstopglobal", "OnBetStopGlobal");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<IncomingBetStopGlobalMessage>(golden);
            ContractAssert.NoDefaults(message);
        });
    }
}
