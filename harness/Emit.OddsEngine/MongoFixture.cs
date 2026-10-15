using Contracts.Harness;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Emit.OddsEngine;

/// <summary>
/// A MongoDB container for the OE.Sportsbook.Service publish paths that read or write the
/// Sportsbook catalogue before publishing. Started only when goldens are being written
/// (CONTRACT_GOLDEN_DIR set), so a plain dotnet test with no goldens needs no Docker.
/// </summary>
/// <remarks>
/// mongo:8.0.13 matches the dev MongoDB (infrastructure/helmfile/common/mongodb.yaml,
/// bitnamilegacy/mongodb 8.0.13). The container's connection string is acknowledged, unlike
/// production's w=0, so the read-after-write on the settlement path cannot race.
/// </remarks>
public sealed class MongoFixture : IAsyncLifetime
{
    private MongoDbContainer? _container;

    public string ConnectionString =>
        _container?.GetConnectionString() ?? throw new InvalidOperationException("Mongo is not started.");

    public IMongoDatabase Sportsbook => new MongoClient(ConnectionString).GetDatabase("Sportsbook");

    public async Task InitializeAsync()
    {
        if (GoldenStore.FromEnvironment() is null)
            return;

        _container = new MongoDbBuilder("mongo:8.0.13").Build();
        await _container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
