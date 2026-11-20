using Pos.NodeService.Data;

namespace Pos.IntegrationTests;

/// <summary>No database needed.</summary>
public class ServerVersionCheckTests
{
    [Theory]
    [InlineData("8.4.7", "8.4.7")]
    [InlineData("8.0.19", "8.0.19")]
    [InlineData("8.0.36-0ubuntu0.22.04.1", "8.0.36")]
    [InlineData("9.1.0", "9.1.0")]
    public void Supported_mysql_versions(string raw, string version)
    {
        var result = ServerVersionCheck.Evaluate(raw);

        Assert.True(result.Supported, result.Reason);
        Assert.Equal("MySQL", result.Product);
        Assert.Equal(Version.Parse(version), result.Version);
    }

    [Theory]
    [InlineData("8.0.18")]
    [InlineData("5.7.44-log")]
    public void Old_mysql_is_rejected(string raw)
    {
        var result = ServerVersionCheck.Evaluate(raw);

        Assert.False(result.Supported);
        Assert.Contains("8.0.19", result.Reason);
    }

    [Theory]
    [InlineData("11.4.9-MariaDB")]
    [InlineData("10.11.6-MariaDB-log")]
    [InlineData("5.5.5-10.6.12-MariaDB")]
    public void MariaDB_is_rejected(string raw)
    {
        var result = ServerVersionCheck.Evaluate(raw);

        Assert.False(result.Supported);
        Assert.Equal("MariaDB", result.Product);
        Assert.Contains("MariaDB", result.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    public void Unparseable_version_is_rejected(string raw)
    {
        Assert.False(ServerVersionCheck.Evaluate(raw).Supported);
    }
}
