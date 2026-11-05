using TokenTracker.Core.Pricing;
using TokenTracker.Core.Session;
using TokenTracker.Core.Storage;

namespace TokenTracker.Server;

// The system drawer inspects read-only VS Code metadata paths, which is a lot of
// filesystem plumbing; keeping it out of Program.cs leaves the routing table
// readable.
public static class SystemInfoBuilder
{
    public static SystemInfoPayload Build(
        ServerInfoPayload serverInfo,
        SessionMetadataProvider metadata,
        SpanStore store,
        ReceiverCounters counters,
        ServerSettings settings,
        string dataDirectory,
        string endpoint)
    {
        var ahp = metadata.GetAhpStatus();
        var lastReceived = store.GetLastReceivedAtMs();
        var access = metadata.GetAccessInfo();
        var watchedPaths = new List<SystemPathInfo>();

        void AddPath(string key, string? path, string kind, string mode)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                watchedPaths.Add(new SystemPathInfo(
                    key,
                    "-",
                    kind,
                    false,
                    null,
                    "unused"));
                return;
            }

            var exists = kind == "directory"
                ? Directory.Exists(path)
                : File.Exists(path);
            string? modified = null;
            if (exists)
            {
                try
                {
                    modified = (kind == "directory"
                            ? Directory.GetLastWriteTime(path)
                            : File.GetLastWriteTime(path))
                        .ToString("yyyy-MM-dd HH:mm:ss");
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            watchedPaths.Add(new SystemPathInfo(
                key,
                path,
                kind,
                exists,
                modified,
                exists ? MonitorKey(mode) : "unused"));
        }

        string MonitorKey(string mode) => mode switch
        {
            "watch" => ahp.Connected ? "paused-ahp" : "watching",
            "poll" => "polling",
            "import" => "import-only",
            _ => "on-query",
        };

        AddPath(
            "session-state",
            access.SessionRoot,
            "directory",
            "watch");
        AddPath(
            "agent-host-db",
            access.AgentHostDatabase,
            "file",
            "watch");
        foreach (var directory in access.CopilotUserDataDirectories)
        {
            AddPath(
                "chat-title-index",
                Path.Combine(directory, "globalStorage", "state.vscdb"),
                "file",
                "read");
            AddPath(
                "workspace-title-index",
                Path.Combine(directory, "workspaceStorage"),
                "directory",
                "read");
            AddPath(
                "copilot-trace-db",
                Path.Combine(
                    directory,
                    "globalStorage",
                    "github.copilot-chat",
                    "agent-traces.db"),
                "file",
                "import");
        }

        foreach (var directory in access.AhpUserDataDirectories)
        {
            AddPath(
                "ahp-endpoint-dir",
                Path.Combine(
                    directory,
                    "agent-host",
                    "local-endpoint",
                    "entries"),
                "directory",
                "poll");
            AddPath(
                "agent-host-trace-db",
                Path.Combine(directory, "agent-host", "otel", "agent-host-traces.db"),
                "file",
                "import");
        }

        var holidayApi =
            Environment.GetEnvironmentVariable("TOKENTRACKER_HOLIDAY_API_URL") ??
            HolidayService.DefaultUrlTemplate;
        var accesses = new List<SystemAccessInfo>
        {
            new("holiday-api", holidayApi, "http"),
            new("otlp-endpoint", endpoint, "http"),
            new("ahp-title-sync", "named pipe / UDS / TCP", "ipc"),
        };

        return new SystemInfoPayload(
            serverInfo,
            store.GetStorageStats(),
            counters.Snapshot(endpoint, running: true),
            settings.RetentionDays,
            dataDirectory,
            lastReceived > 0
                ? DateTimeOffset.FromUnixTimeMilliseconds(lastReceived)
                    .ToLocalTime()
                    .ToString("yyyy-MM-dd HH:mm:ss")
                : "-",
            new SystemAhpInfo(ahp.State, ahp.Connected, ahp.Sessions),
            watchedPaths,
            accesses);
    }
}
