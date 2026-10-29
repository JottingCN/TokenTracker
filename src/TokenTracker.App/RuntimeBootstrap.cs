using System.Runtime.CompilerServices;

namespace TokenTracker_App;

internal static class RuntimeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize() =>
        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);
}
