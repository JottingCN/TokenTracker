using System.Text.Json;
using Microsoft.Data.Sqlite;
using TokenTracker.Core.Models;
using TokenTracker.Core.Session;

namespace TokenTracker.Core.Tests;

public sealed class SessionMetadataProviderTests
{
    [Fact]
    public void ReadsTitleAndUserNamedFlag()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-1",
                """
                id: session-1
                cwd: d:\source\demo
                name: 调试缓存命中率
                user_named: true
                """);

            var provider = new SessionMetadataProvider(root, System.IO.Path.Combine(root, "agent-host.db"));
            var metadata = provider.Get("session-1");

            Assert.NotNull(metadata);
            Assert.Equal("调试缓存命中率", metadata!.Title);
            Assert.True(metadata.UserNamed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadsCopilotChatSessionStoreTitle()
    {
        var root = CreateRoot();
        var userData = System.IO.Path.Combine(root, "user-data");
        var workspaceStorage = System.IO.Path.Combine(
            userData,
            "workspaceStorage",
            "workspace-1");
        var stateDatabase = System.IO.Path.Combine(workspaceStorage, "state.vscdb");

        try
        {
            Directory.CreateDirectory(workspaceStorage);
            using (var connection = new SqliteConnection($"Data Source={stateDatabase}"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ItemTable (key TEXT UNIQUE ON CONFLICT REPLACE, value BLOB);
                    INSERT INTO ItemTable (key, value) VALUES (@key, @value);
                    """;
                command.Parameters.AddWithValue("@key", "chat.ChatSessionStore.index");
                command.Parameters.AddWithValue(
                    "@value",
                    """
                    {"version":1,"entries":{"chat-session":{"sessionId":"chat-session","title":"Extension chat title","lastMessageDate":123,"workingDirectory":"file:///d:/source/demo"}}}
                    """);
                command.ExecuteNonQuery();
            }

            var provider = new SessionMetadataProvider(
                root,
                System.IO.Path.Combine(root, "missing-agent-host.db"),
                new[] { userData });
            var metadata = provider.Get("chat-session");

            Assert.NotNull(metadata);
            Assert.Equal("Extension chat title", metadata!.Title);
            Assert.False(metadata.UserNamed);
            Assert.EndsWith(
                "demo",
                metadata.WorkspacePath,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SupportsQuotedTitlesAndRefreshAfterChange()
    {
        var root = CreateRoot();
        try
        {
            var file = WriteWorkspace(
                root,
                "session-2",
                """
                name: "A: B"
                cwd: 'd:\work\app'
                """);

            var provider = new SessionMetadataProvider(root, System.IO.Path.Combine(root, "agent-host.db"));
            Assert.Equal("A: B", provider.Get("session-2")!.Title);
            Assert.Equal(@"d:\work\app", provider.Get("session-2")!.WorkspacePath);

            File.WriteAllText(file, "name: 新标题\ncwd: d:\\work\\app2\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(5));

            Assert.Equal("新标题", provider.Get("session-2")!.Title);
            Assert.Equal(@"d:\work\app2", provider.Get("session-2")!.WorkspacePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void PrefersAgentHostTitleOverWorkspaceAutoTitle()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-3",
                """
                name: yaml 名称
                user_named: false
                """);
            var database = CreateAgentHostDatabase(
                root,
                "session-3",
                "agent 标题",
                "auto");

            var provider = new SessionMetadataProvider(root, database);

            Assert.Equal("agent 标题", provider.Get("session-3")!.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void KeepsWorkspaceUserNamedTitleOverAgentAutoTitle()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-4",
                """
                name: yaml 命名
                user_named: true
                """);
            var database = CreateAgentHostDatabase(
                root,
                "session-4",
                "agent 标题",
                "auto");

            var provider = new SessionMetadataProvider(root, database);

            Assert.Equal("yaml 命名", provider.Get("session-4")!.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AgentUserNamedTitleWinsOverWorkspaceTitle()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-5",
                """
                name: yaml 命名
                user_named: true
                """);
            var database = CreateAgentHostDatabase(
                root,
                "session-5",
                "agent 命名",
                "user");

            var provider = new SessionMetadataProvider(root, database);

            Assert.Equal("agent 命名", provider.Get("session-5")!.Title);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FallsBackToAgentHostWorkingDirectory()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-6",
                """
                name: 仅名称
                user_named: false
                """);
            var database = CreateAgentHostDatabase(
                root,
                "session-6",
                "agent 标题",
                "auto",
                workingDirectories: "[\"file:///D%3A/source/demo\"]");

            var provider = new SessionMetadataProvider(root, database);

            Assert.Equal(
                @"D:\source\demo",
                provider.Get("session-6")!.WorkspacePath,
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AgentHostDirectoryWinsOverWorkspaceYamlCwd()
    {
        var root = CreateRoot();
        try
        {
            WriteWorkspace(
                root,
                "session-7",
                """
                name: 仅名称
                cwd: D:\yaml\project
                """);
            var database = CreateAgentHostDatabase(
                root,
                "session-7",
                "agent 标题",
                "auto",
                workingDirectories: "[\"file:///D%3A/agent/project\"]");

            var provider = new SessionMetadataProvider(root, database);

            Assert.Equal(
                @"D:\agent\project",
                provider.Get("session-7")!.WorkspacePath,
                ignoreCase: true);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReturnsNullForMissingOrUnsafeSessions()
    {
        var root = CreateRoot();
        try
        {
            var provider = new SessionMetadataProvider(root, System.IO.Path.Combine(root, "agent-host.db"));
            Assert.Null(provider.Get("missing"));
            Assert.Null(provider.Get(".."));
            Assert.Null(provider.Get(@"..\evil"));
            Assert.Null(provider.Get(""));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void RowFallsBackToUnnamedSessionWithoutTitle()
    {
        var row = new SessionRow
        {
            SessionId = "12345678-1234-1234-1234-123456789abc",
            Model = "deepseek-flash",
        };

        Assert.Equal("未命名 · 12345678…", row.SessionDisplay);

        var titled = row with { Title = "标题" };
        Assert.Equal("标题", titled.SessionDisplay);
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "tokentracker-metadata-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteWorkspace(string root, string sessionId, string content)
    {
        var directory = Path.Combine(root, sessionId);
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "workspace.yaml");
        File.WriteAllText(file, content);
        return file;
    }

    private static string CreateAgentHostDatabase(
        string root,
        string sessionId,
        string summary,
        string titleSource,
        string? workingDirectories = null,
        string? launchDirectories = null)
    {
        var path = Path.Combine(root, "agent-host.db");
        using var connection = new SqliteConnection(
            $"Data Source={path};Pooling=False");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE chats_v2 (
                chat_uri TEXT PRIMARY KEY,
                owner_session_uri TEXT NOT NULL,
                chat_order INTEGER,
                provider_data TEXT,
                metadata TEXT NOT NULL);
            CREATE TABLE session_chat_state (
                session_uri TEXT NOT NULL,
                chat_uri TEXT NOT NULL,
                is_default INTEGER NOT NULL,
                title TEXT,
                title_source TEXT,
                working_directories TEXT,
                launch_working_directories TEXT);
            INSERT INTO chats_v2
                (chat_uri, owner_session_uri, chat_order, provider_data, metadata)
            VALUES
                ('chat-1', 'copilotcli:/' || @id, 0, @provider, @metadata);
            INSERT INTO session_chat_state
                (session_uri, chat_uri, is_default, title, title_source,
                 working_directories, launch_working_directories)
            VALUES
                ('copilotcli:/' || @id, 'chat-1', 1, NULL, NULL, @working, @launch);
            """;
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue(
            "@provider",
            $"{{\"sdkSessionId\":\"{sessionId}\"}}");
        command.Parameters.AddWithValue(
            "@metadata",
            JsonSerializer.Serialize(new
            {
                data = new { summary, titleSource },
            }));
        command.Parameters.AddWithValue(
            "@working",
            (object?)workingDirectories ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@launch",
            (object?)launchDirectories ?? DBNull.Value);
        command.ExecuteNonQuery();

        return path;
    }
}
