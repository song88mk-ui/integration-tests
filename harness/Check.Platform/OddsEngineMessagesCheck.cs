using Contracts.Harness;
using Platform.CashOutStake.Servicev2.Repository;
using Platform.Shared.MessageModels;

namespace Check.Platform;

/// <summary>
/// What Platform.Settlement.Service and Platform.CashOutStake.Servicev2 read from the
/// OE.Sportsbook.Service goldens, deserialized exactly as their Workers do.
/// </summary>
public class OddsEngineMessagesCheck
{
    private static GoldenStore Store => GoldenStore.FromEnvironment()!;

    /// <summary>
    /// Platform.Settlement.Service Worker.cs:110. Throws today: OE sends MarketId as a JSON
    /// string and OnUOFBetCancellation.MarketId is a long. Deferred in GM-959 (decision in bet-genius docs/ArchitectureDecisions.md): OE.Sportsbook.Service
    /// does not publish this message, and the contract is fixed together with the Worker case if
    /// it is ever restored. The deferred work is tracked in GM-962.
    /// </summary>
    [KnownDefectFact("GM-962")]
    public void Settlement_OnBetCancellation_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.bet.settlement", "OnBetCancellation");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnUOFBetCancellation>(golden);
            ContractAssert.NoDefaults(message, allowed:
            [
                "SettlementScore", // OE hard-codes string.Empty (SportsbookRepository.cs:310)
            ]);
        });
    }

    /// <summary>
    /// Platform.CashOutStake.Servicev2 Worker.cs:68. OnExternalOddsChangeMessage uses the OE v1
    /// long names (FixtureId, Markets) and OE2 sends short ones (FiId, Ma), so nothing is read.
    /// </summary>
    [KnownDefectFact("GM-902")]
    public void CashOutStake_OnOddsChange_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.sb.engine.onoddschange", "OnOddsChange");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnExternalOddsChangeMessage>(golden);
            ContractAssert.NoDefaults(message);
        });
    }

    /// <summary>
    /// Platform.CashOutStake.Servicev2 Worker.cs:77-78 reads OnBetCancellation as OnBetSettlement,
    /// a different shape, so Bet is null and RemoveBet throws (BettingRepository.cs:246).
    /// Deferred in GM-959 (decision in bet-genius docs/ArchitectureDecisions.md): OE.Sportsbook.Service
    /// does not publish this message, and the contract is fixed together with the Worker case if
    /// it is ever restored. The deferred work is tracked in GM-962.
    /// </summary>
    [KnownDefectFact("GM-962")]
    public void CashOutStake_OnBetCancellation_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.bet.settlement", "OnBetCancellation");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnBetSettlement>(golden);
            ContractAssert.NoDefaults(message);
        });
    }

    /// <summary>Platform.Settlement.Service Worker.cs:103, for both OE builders.</summary>
    [GoldenFact]
    public void Settlement_OnBetSettlement_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.bet.settlement", "OnBetSettlement");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnUOFBetSettlement>(golden);
            ContractAssert.NoDefaults(message, allowed:
            [
                // Optional: a settlement window that only applies when both are > 0
                // (Settlement BettingRepository.cs:180-189). OE2 never sends one.
                "StartTime", "EndTime",
                // Optional: copied only when not empty (:972). OE2 stopped sending it
                // (//SettlementScore = message.Sc, OE SportsbookRepository.cs).
                "SettlementScore",
            ]);
        });
    }

    /// <summary>
    /// Platform.CashOutStake.Servicev2 Worker.cs:83-86 reads OnBetSettlement on
    /// r.oe.bet.settlement as OnExternalBetSettlementMessage, for both OE builders.
    /// </summary>
    [GoldenFact]
    public void CashOutStake_OnBetSettlement_ReadsEveryField()
    {
        // Arrange
        var goldens = Store.ReadAll("r.oe.bet.settlement", "OnBetSettlement");

        // Act + Assert: every producer variant
        ContractAssert.ForEachGolden(goldens, golden =>
        {
            var message = ConsumerJson.Deserialize<OnExternalBetSettlementMessage>(golden);
            ContractAssert.NoDefaults(message);
        });
    }
}
