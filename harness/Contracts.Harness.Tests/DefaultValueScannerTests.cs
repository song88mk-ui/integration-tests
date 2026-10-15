using Contracts.Harness;

namespace Contracts.Harness.Tests;

public class DefaultValueScannerTests
{
    private enum Status { Unknown, Open }

    private sealed class Selection
    {
        public long SeId { get; set; }
        public decimal Od { get; set; }
    }

    private sealed class Market
    {
        public string? MaNa { get; set; }
        public Status Status { get; set; }
        public List<Selection> Se { get; set; } = [];
    }

    private sealed class Fixture
    {
        public long FiId { get; set; }
        public string? FiNa { get; set; }
        public DateTime StartTime { get; set; }
        public List<Market> Ma { get; set; } = [];
        public Dictionary<string, Selection> ByCode { get; set; } = [];
        public Fixture? Parent { get; set; }
    }

    private static Fixture Populated() => new()
    {
        FiId = 42,
        FiNa = "Home v Away",
        StartTime = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc),
        Ma = [new Market { MaNa = "1X2", Status = Status.Open, Se = [new Selection { SeId = 7, Od = 1.95m }] }],
        ByCode = new() { ["H"] = new Selection { SeId = 8, Od = 2.10m } },
    };

    [Fact]
    public void Scan_FullyPopulatedObject_ReportsOnlyTheNullParent()
    {
        // Arrange
        var fixture = Populated();

        // Act
        var defaults = DefaultValueScanner.Scan(fixture);

        // Assert
        Assert.Equal(["Parent"], defaults);
    }

    [Fact]
    public void Scan_DefaultScalars_ReportsEachPath()
    {
        // Arrange
        var fixture = Populated();
        fixture.FiId = 0;
        fixture.FiNa = "";
        fixture.StartTime = default;

        // Act
        var defaults = DefaultValueScanner.Scan(fixture, allowed: ["Parent"]);

        // Assert
        Assert.Equal(["FiId", "FiNa", "StartTime"], defaults);
    }

    [Fact]
    public void Scan_DefaultInsideListElements_ReportsIndexlessPathOnce()
    {
        // Arrange
        var fixture = Populated();
        fixture.Ma.Add(new Market { MaNa = "OU", Status = Status.Open, Se = [new Selection { SeId = 9 }] });
        fixture.Ma[0].Se[0].Od = 0;

        // Act
        var defaults = DefaultValueScanner.Scan(fixture, allowed: ["Parent"]);

        // Assert
        Assert.Equal(["Ma[].Se[].Od"], defaults);
    }

    [Fact]
    public void Scan_EnumZeroAndEmptyCollection_AreReported()
    {
        // Arrange
        var fixture = Populated();
        fixture.Ma[0].Status = Status.Unknown;
        fixture.ByCode.Clear();

        // Act
        var defaults = DefaultValueScanner.Scan(fixture, allowed: ["Parent"]);

        // Assert
        Assert.Equal(["ByCode", "Ma[].Status"], defaults);
    }

    [Fact]
    public void Scan_DictionaryValue_UsesKeyInPath()
    {
        // Arrange
        var fixture = Populated();
        fixture.ByCode["H"].SeId = 0;

        // Act
        var defaults = DefaultValueScanner.Scan(fixture, allowed: ["Parent"]);

        // Assert
        Assert.Equal(["ByCode[H].SeId"], defaults);
    }

    [Fact]
    public void Scan_SelfReference_Terminates()
    {
        // Arrange
        var fixture = Populated();
        fixture.Parent = fixture;

        // Act
        var defaults = DefaultValueScanner.Scan(fixture);

        // Assert
        Assert.Empty(defaults);
    }

    [Fact]
    public void Scan_NullRoot_ReportsRoot()
    {
        // Act
        var defaults = DefaultValueScanner.Scan(null);

        // Assert
        Assert.Equal(["$"], defaults);
    }
}
