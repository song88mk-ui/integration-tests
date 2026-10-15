using Contracts.Harness;

namespace Contracts.Harness.Tests;

public class SamplePopulatorTests
{
    private enum Status { Unknown, Open, Closed }

    private sealed class Selection
    {
        public long SeId { get; set; }
        public decimal? Od { get; set; }
        public string? SeNa { get; set; }
    }

    private sealed class Market
    {
        public int MaId { get; set; }
        public Status MaSt { get; set; }
        public List<Selection> Se { get; set; } = [];
        public Dictionary<string, int> Tr { get; set; } = [];
        public int[] Li { get; set; } = [];
        public string Computed => "read-only";
    }

    private sealed class Fixture
    {
        public long FiId { get; set; }
        public bool IsLive { get; set; }
        public DateTime FiStDa { get; set; }
        public IList<Market> Ma { get; set; } = [];
        public Fixture? Parent { get; set; }
    }

    [Fact]
    public void Fill_NestedModel_LeavesOnlyTheCyclicReferenceDefault()
    {
        // Act
        var fixture = SamplePopulator.Fill<Fixture>();

        // Assert
        Assert.Equal(["Parent"], DefaultValueScanner.Scan(fixture));
    }

    [Fact]
    public void Fill_EnumWithZeroMember_TakesFirstNonZeroValue()
    {
        // Act
        var market = SamplePopulator.Fill<Market>();

        // Assert
        Assert.Equal(Status.Open, market.MaSt);
    }

    [Fact]
    public void Fill_Strings_AreDistinctAndNamedAfterTheMember()
    {
        // Act
        var market = SamplePopulator.Fill<Market>();
        var first = market.Se[0].SeNa;
        var key = market.Tr.Keys.Single();

        // Assert
        Assert.StartsWith("SeNa-", first);
        Assert.StartsWith("Tr-", key);
        Assert.NotEqual(first, key);
    }

    [Fact]
    public void Fill_Numbers_AreDistinctAcrossMembers()
    {
        // Act
        var market = SamplePopulator.Fill<Market>();

        // Assert
        Assert.NotEqual(market.MaId, market.Li[0]);
        Assert.NotEqual(market.Se[0].SeId, market.Se[0].Od);
    }
}
