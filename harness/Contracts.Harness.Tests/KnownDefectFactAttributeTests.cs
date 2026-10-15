using Contracts.Harness;

namespace Contracts.Harness.Tests;

// These tests change process environment variables, so they run outside the parallel pool.
[CollectionDefinition(nameof(EnvironmentCollection), DisableParallelization = true)]
public sealed class EnvironmentCollection;

[Collection(nameof(EnvironmentCollection))]
public sealed class KnownDefectFactAttributeTests : IDisposable
{
    private readonly string? _include = Environment.GetEnvironmentVariable(KnownDefectFactAttribute.IncludeVariable);
    private readonly string? _goldens = Environment.GetEnvironmentVariable(GoldenStore.DirectoryVariable);

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(KnownDefectFactAttribute.IncludeVariable, _include);
        Environment.SetEnvironmentVariable(GoldenStore.DirectoryVariable, _goldens);
    }

    [Fact]
    public void Constructor_WithoutInclude_SkipsWithTicketKey()
    {
        // Arrange
        Environment.SetEnvironmentVariable(KnownDefectFactAttribute.IncludeVariable, null);
        Environment.SetEnvironmentVariable(GoldenStore.DirectoryVariable, "/tmp/goldens");

        // Act
        var attribute = new KnownDefectFactAttribute("GM-902");

        // Assert
        Assert.Contains("GM-902", attribute.Skip);
    }

    [Fact]
    public void Constructor_WithIncludeAndGoldens_Runs()
    {
        // Arrange
        Environment.SetEnvironmentVariable(KnownDefectFactAttribute.IncludeVariable, "1");
        Environment.SetEnvironmentVariable(GoldenStore.DirectoryVariable, "/tmp/goldens");

        // Act
        var attribute = new KnownDefectFactAttribute("GM-902");

        // Assert
        Assert.Null(attribute.Skip);
    }

    [Fact]
    public void Constructor_WithIncludeButNoGoldens_SkipsForGoldens()
    {
        // Arrange
        Environment.SetEnvironmentVariable(KnownDefectFactAttribute.IncludeVariable, "1");
        Environment.SetEnvironmentVariable(GoldenStore.DirectoryVariable, null);

        // Act
        var attribute = new KnownDefectFactAttribute("GM-902");

        // Assert
        Assert.Contains(GoldenStore.DirectoryVariable, attribute.Skip);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_BlankTicket_Throws(string ticket)
    {
        Assert.Throws<ArgumentException>(() => new KnownDefectFactAttribute(ticket));
    }
}
