using Scan.Assemblies.Tests.Samples;

namespace Scan.Assemblies.Tests;

public class ImageScannerTests
{
    private static readonly (List<Publication> Publications, bool Subscribes) Scanned =
        ImageScanner.ScanAssembly(typeof(Producer).Assembly.Location);

    private static HashSet<string> Keys() =>
        Scanned.Publications.Select(p => p.Key).Where(k => k.StartsWith("r.sample.")).ToHashSet();

    [Theory]
    [InlineData("r.sample.literal")]
    [InlineData("r.sample.cpublish")]
    [InlineData("r.sample.engine.onoddschange")]  // const + ToLower resolved
    [InlineData("r.sample.error.const")]          // const field inlined
    [InlineData("r.sample.betstatus.*")]          // unknown concatenated part
    [InlineData("r.sample.format.*.end")]         // string.Format with an unknown argument
    [InlineData("r.sample.loop.*")]               // loop variable
    [InlineData("r.sample.local.10")]             // local assigned once
    [InlineData("r.sample.branch.*")]             // local assigned on two branches
    [InlineData("r.sample.async.onasync")]        // async state machine local
    [InlineData("r.sample.gateway")]              // RoutingKey setter
    public void ScanAssembly_FindsPublishedKey(string key)
    {
        // Act
        var keys = Keys();

        // Assert
        Assert.Contains(key, keys);
    }

    [Theory]
    [InlineData("r.sample.external")]    // ExternalPublish goes to the external broker
    [InlineData("r.sample.compare")]     // comparisons read a key, they do not publish it
    [InlineData("r.sample.startswith")]
    [InlineData("r.sample.branch.first")]
    [InlineData("r.sample.branch.second")]
    public void ScanAssembly_IgnoresKeyThatIsNotPublished(string key)
    {
        // Act
        var keys = Keys();

        // Assert
        Assert.DoesNotContain(key, keys);
    }

    [Fact]
    public void ScanAssembly_RecordsWhereEachKeyIsPublished()
    {
        // Act
        var literal = Scanned.Publications.Single(p => p.Key == "r.sample.literal");

        // Assert
        Assert.Equal("Scan.Assemblies.Tests", literal.Assembly);
        Assert.Equal("Scan.Assemblies.Tests.Samples.Producer.Literal", literal.Method);
    }

    [Fact]
    public void ScanAssembly_DetectsMessageReceivedSubscription()
    {
        // Assert
        Assert.True(Scanned.Subscribes);
    }

    [Fact]
    public void Scan_ImageWithoutRuntimeConfig_HasNoEntryAndNoPublications()
    {
        // Arrange
        var dir = Directory.CreateTempSubdirectory("scan-empty-");

        try
        {
            // Act
            var scan = ImageScanner.Scan(dir.FullName);

            // Assert
            Assert.Null(scan.Entry);
            Assert.False(scan.Subscribes);
            Assert.Empty(scan.Publications);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}
