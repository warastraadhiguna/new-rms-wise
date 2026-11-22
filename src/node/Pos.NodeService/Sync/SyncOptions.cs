namespace Pos.NodeService.Sync;

/// <summary>Section <c>Sync</c> of the configuration (appsettings.Local.json). The token is not here: see <see cref="SyncTokenStore"/>.</summary>
public sealed class SyncOptions
{
    /// <summary>HQ base URL, e.g. https://hq.example.com (the API is under /api/v1/sync). Empty = sync off.</summary>
    public string? HqUrl { get; set; }

    /// <summary>The DPAPI-encrypted token file. Default %ProgramData%\PosNodeService\keys\sync-token.dat.</summary>
    public string? TokenPath { get; set; }

    public bool Enabled { get; set; } = true;

    /// <summary>Run the background worker (tests switch it off and drive <see cref="SyncAgent"/> themselves).</summary>
    public bool Worker { get; set; } = true;

    /// <summary>Pause between sync cycles when everything went well.</summary>
    public int IntervalSeconds { get; set; } = 30;

    /// <summary>Longest pause after failures (no internet, HQ down): the pause doubles up to this.</summary>
    public int MaxBackoffSeconds { get; set; } = 300;

    public int PushBatchSize { get; set; } = 100;

    public int PullLimit { get; set; } = 500;

    public int RequestTimeoutSeconds { get; set; } = 60;

    public string TokenFile => string.IsNullOrWhiteSpace(TokenPath) ? SyncTokenStore.DefaultPath : TokenPath;

    public bool HasHq => !string.IsNullOrWhiteSpace(HqUrl);
}
