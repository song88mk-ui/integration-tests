using Contracts.Harness;

namespace Contracts.Harness.Tests;

public sealed class GoldenStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "contract-goldens-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Write_ThenReadAll_RoundTripsTheGolden()
    {
        // Arrange
        var store = new GoldenStore(_root);
        var golden = new Golden("r.oe.bet.settlement", "OnBetSettlement", """{"MarketId":1}""");

        // Act
        var path = store.Write(golden);
        var read = store.ReadAll("r.oe.bet.settlement", "OnBetSettlement");

        // Assert
        Assert.Equal(Path.Combine(_root, "r.oe.bet.settlement", "OnBetSettlement.json"), path);
        Assert.Equal([golden], read);
    }

    [Fact]
    public void ReadAll_SeveralVariants_ReturnsEachAndNoOtherType()
    {
        // Arrange
        var store = new GoldenStore(_root);
        store.Write(new Golden("r.oe.bet.settlement", "OnBetSettlement", "{}", "FromMarketSettlement"));
        store.Write(new Golden("r.oe.bet.settlement", "OnBetSettlement", "{}", "FromBetSettlement"));
        store.Write(new Golden("r.oe.bet.settlement", "OnBetCancellation", "{}"));

        // Act
        var read = store.ReadAll("r.oe.bet.settlement", "OnBetSettlement");

        // Assert
        Assert.Equal(["FromBetSettlement", "FromMarketSettlement"], read.Select(g => g.Variant));
    }

    [Fact]
    public void ReadAll_MissingGolden_Throws()
    {
        // Arrange
        var store = new GoldenStore(_root);

        // Act + Assert
        var error = Assert.Throws<FileNotFoundException>(() => store.ReadAll("r.bet.placement", "OnBetPlacement"));
        Assert.Contains("Run the emitters first", error.Message);
    }

    [Theory]
    [InlineData("r/bet", "OnBetPlacement", "")]
    [InlineData("..", "OnBetPlacement", "")]
    [InlineData("r.bet.placement", "../OnBetPlacement", "")]
    [InlineData("r.bet.placement", "", "")]
    [InlineData("r.bet.placement", "OnBetPlacement", "a~b")]
    [InlineData("r.bet.placement", "OnBetPlacement", "../x")]
    public void Write_UnsafePathSegment_Throws(string routingKey, string messageType, string variant)
    {
        // Arrange
        var store = new GoldenStore(_root);

        // Act + Assert
        Assert.Throws<ArgumentException>(() => store.Write(new Golden(routingKey, messageType, "{}", variant)));
        Assert.False(Directory.Exists(_root));
    }
}
