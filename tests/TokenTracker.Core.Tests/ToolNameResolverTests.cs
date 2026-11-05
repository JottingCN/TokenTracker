using System.Security.Cryptography;
using System.Text;
using TokenTracker.Core.Otlp;

namespace TokenTracker.Core.Tests;

public sealed class ToolNameResolverTests
{
    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    [Fact]
    public void ResolvesHashedBuiltInToolNames()
    {
        Assert.Equal(
            "clickElement",
            ToolNameResolver.Resolve(
                "9273300dd6331d1cda648f7a3c7f2784523a9c8d63b723907d69c18135c12185"));
        Assert.Equal("screenshotPage", ToolNameResolver.Resolve(Hash("screenshotPage")));
        Assert.Equal("runPlaywrightCode", ToolNameResolver.Resolve(Hash("runPlaywrightCode")));
        // The hash is derived, so an upper-case value resolves to the same name.
        Assert.Equal(
            "clickElement",
            ToolNameResolver.Resolve(Hash("clickElement").ToUpperInvariant()));
    }

    [Fact]
    public void LeavesPlainAndUnknownNamesAlone()
    {
        Assert.Null(ToolNameResolver.Resolve("read_powershell"));
        Assert.Null(ToolNameResolver.Resolve(null));
        Assert.Null(ToolNameResolver.Resolve(string.Empty));
        Assert.Null(ToolNameResolver.Resolve(new string('f', 64)));
        Assert.Null(ToolNameResolver.Resolve(new string('z', 64)));

        Assert.Equal("read_powershell", ToolNameResolver.Display("read_powershell"));
        Assert.Equal(
            new string('f', 64),
            ToolNameResolver.Display(new string('f', 64)));
        Assert.Equal("-", ToolNameResolver.Display(null));
    }
}
