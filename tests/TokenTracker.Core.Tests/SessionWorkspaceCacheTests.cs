using Microsoft.Data.Sqlite;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;

namespace TokenTracker.Core.Tests;

public sealed class SessionWorkspaceCacheTests
{
    [Fact]
    public void LivePathIsPersistedAndReusedWhenMetadataDisappears()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new SpanStore(databasePath);
            var cache = new SessionWorkspaceCache(store);

            Assert.Equal(
                @"d:\work\alpha",
                cache.Resolve("session-1", @"d:\work\alpha"));

            // Writes are batched, so nothing has hit the database yet.
            Assert.Null(store.GetSessionWorkspace("session-1"));

            cache.Flush();
            Assert.Equal(@"d:\work\alpha", store.GetSessionWorkspace("session-1"));

            // Agent Host offline: no live path, the cached one is still returned.
            Assert.Equal(
                @"d:\work\alpha",
                cache.Resolve("session-1", liveWorkspacePath: null));
            Assert.Equal(
                @"d:\work\alpha",
                cache.Resolve("session-1", "   "));

            Assert.Null(cache.Resolve("session-unknown", null));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void PendingPathsFlushAutomaticallyAtTheBatchThreshold()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-tests",
            Guid.NewGuid().ToString("N"));
        var databasePath = Path.Combine(directory, "tokentracker.db");

        try
        {
            Directory.CreateDirectory(directory);
            var store = new SpanStore(databasePath);
            var cache = new SessionWorkspaceCache(store);

            for (var index = 0; index < 15; index++)
            {
                cache.Resolve($"session-{index}", $@"d:\work\{index}");
            }

            Assert.Null(store.GetSessionWorkspace("session-0"));

            cache.Resolve("session-15", @"d:\work\15");

            Assert.Equal(@"d:\work\0", store.GetSessionWorkspace("session-0"));
            Assert.Equal(@"d:\work\15", store.GetSessionWorkspace("session-15"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
