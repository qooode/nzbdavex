using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet.Concurrency;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Config;

public class ConfigManager
{
    public static readonly string AppVersion = EnvironmentUtil.GetEnvironmentVariable("NZBDAV_VERSION") ?? "0.0.0";

    private readonly Dictionary<string, string> _config = new();
    public event EventHandler<ConfigEventArgs>? OnConfigChanged;

    public async Task LoadConfig()
    {
        await using var dbContext = new DavDatabaseContext();
        var configItems = await dbContext.ConfigItems.ToListAsync().ConfigureAwait(false);
        lock (_config)
        {
            _config.Clear();
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }
    }

    private string? GetConfigValue(string configName)
    {
        lock (_config)
        {
            return _config.TryGetValue(configName, out string? value) ? value : null;
        }
    }

    private T? GetConfigValue<T>(string configName)
    {
        var rawValue = StringUtil.EmptyToNull(GetConfigValue(configName));
        return rawValue == null ? default : JsonSerializer.Deserialize<T>(rawValue);
    }

    public void UpdateValues(List<ConfigItem> configItems)
    {
        lock (_config)
        {
            foreach (var configItem in configItems)
            {
                _config[configItem.ConfigName] = configItem.ConfigValue;
            }
        }

        var changedConfig = configItems.ToDictionary(x => x.ConfigName, x => x.ConfigValue);
        OnConfigChanged?.Invoke(this, new ConfigEventArgs { ChangedConfig = changedConfig });
    }

    public string GetRcloneMountDir()
    {
        var mountDir = StringUtil.EmptyToNull(GetConfigValue("rclone.mount-dir"))
                       ?? EnvironmentUtil.GetEnvironmentVariable("MOUNT_DIR")
                       ?? "/mnt/nzbdav";
        if (mountDir.EndsWith('/')) mountDir = mountDir.TrimEnd('/');
        return mountDir;
    }

    public string GetApiKey()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.key"))
               ?? EnvironmentUtil.GetRequiredVariable("FRONTEND_BACKEND_API_KEY");
    }

    public string GetStrmKey()
    {
        return GetConfigValue("api.strm-key")
               ?? throw new InvalidOperationException("The `api.strm-key` config does not exist.");
    }

    public List<string> GetApiCategories()
    {
        var value = StringUtil.EmptyToNull(GetConfigValue("api.categories"))
                    ?? EnvironmentUtil.GetEnvironmentVariable("CATEGORIES")
                    ?? "audio,software,tv,movies";

        return value.Split(',')
            .Prepend(GetManualUploadCategory())
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }

    public string GetManualUploadCategory()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.manual-category"))
               ?? "uncategorized";
    }

    public string? GetWebdavUser()
    {
        return StringUtil.EmptyToNull(GetConfigValue("webdav.user"))
               ?? EnvironmentUtil.GetEnvironmentVariable("WEBDAV_USER")
               ?? "admin";
    }

    public string? GetWebdavPasswordHash()
    {
        var hashedPass = StringUtil.EmptyToNull(GetConfigValue("webdav.pass"));
        if (hashedPass != null) return hashedPass;
        var pass = EnvironmentUtil.GetEnvironmentVariable("WEBDAV_PASSWORD");
        if (pass != null) return PasswordUtil.Hash(pass);
        return null;
    }

    public bool IsEnsureImportableVideoEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ensure-importable-video"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool ShowHiddenWebdavFiles()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.show-hidden-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetLibraryDir()
    {
        return StringUtil.EmptyToNull(GetConfigValue("media.library-dir"));
    }

    public int GetMaxDownloadConnections()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.max-download-connections"))
            ?? Math.Min(GetUsenetProviderConfig().TotalPooledConnections, 15).ToString()
        );
    }

    public int GetArticleBufferSize()
    {
        return int.Parse(
            StringUtil.EmptyToNull(GetConfigValue("usenet.article-buffer-size"))
            ?? "40"
        );
    }

    // When true, RAR archives are mounted instantly by parsing only the first
    // volume at import; trailing volumes are resolved on first read. Falls
    // back to eager parsing for archives that don't fit the supported shape
    // (multi-file, solid, encrypted, or compressed).
    public bool IsLazyRarParsingEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("api.lazy-rar-parsing"));
        return v == null || bool.Parse(v);
    }

    public SemaphorePriorityOdds GetStreamingPriority()
    {
        var stringValue = StringUtil.EmptyToNull(GetConfigValue("usenet.streaming-priority"));
        var numericalValue = int.Parse(stringValue ?? "80");
        return new SemaphorePriorityOdds() { HighPriorityOdds = numericalValue };
    }

    public bool IsEnforceReadonlyWebdavEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.enforce-readonly"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public HashSet<string> GetEnsureArticleExistenceCategories()
    {
        var configValue = GetConfigValue("api.ensure-article-existence-categories");
        return (configValue ?? "").Split(',')
            .Select(x => x.Trim())
            .Select(x => x.ToLower())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToHashSet();
    }

    public bool IsPlaybackWatchdogEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.watchdog-enabled"));
        return v == null || bool.Parse(v);
    }

    public int GetPlayTotalBudgetSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.total-budget-seconds"));
        if (v == null) return 30;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 3, 180) : 30;
    }

    public int GetPlayHedgeDelaySeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.hedge-delay-seconds"));
        if (v == null) return 2;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 30) : 2;
    }

    public int GetPlayMaxCandidates()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-candidates"));
        if (v == null) return 1;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 10) : 1;
    }

    public int GetPlayMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.max-attempts"));
        if (v == null) return 15;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 200) : 15;
    }

    public string GetPlayVerifyMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.verify-mode"));
        return v switch
        {
            "body" => "body",
            "none" => "none",
            _ => "stat",
        };
    }

    public TimeSpan GetPlayCandidateNegativeCacheTtl()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("play.candidate-negative-cache-minutes"));
        if (v == null) return TimeSpan.FromMinutes(30);
        return int.TryParse(v, out var n) ? TimeSpan.FromMinutes(Math.Clamp(n, 1, 60 * 24)) : TimeSpan.FromMinutes(30);
    }

    // Newline-separated regex patterns. Candidates whose title matches ANY pattern are
    // skipped before NZB fetch. Case-insensitive by default — use inline (?-i:...) for
    // case-sensitive. Lines starting with '#' are comments. Malformed patterns are
    // dropped (the settings UI rejects them at save time, this is defense in depth).
    public IReadOnlyList<Regex> GetPlayExcludePatterns()
    {
        var raw = GetConfigValue("play.exclude-patterns");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<Regex>();

        var patterns = new List<Regex>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            try
            {
                patterns.Add(new Regex(
                    trimmed,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled,
                    TimeSpan.FromMilliseconds(50)));
            }
            catch (ArgumentException e)
            {
                Log.Warning("Skipping invalid play.exclude-patterns regex {Pattern}: {Message}", trimmed, e.Message);
            }
        }
        return patterns;
    }

    public string GetVariantsMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.mode"));
        return v switch
        {
            "smart" => "smart",
            "collect-all" => "collect-all",
            _ => "off",
        };
    }

    public int GetVariantsTolerancePct()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.tolerance-pct"));
        if (v == null) return 25;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 100) : 25;
    }

    public int GetVariantsMaxPerGroup()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.max-per-group"));
        if (v == null) return 3;
        return int.TryParse(v, out var n) ? Math.Max(0, n) : 3;
    }

    public string GetVariantsReplayStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.replay-strategy"));
        return v switch
        {
            "largest" => "largest",
            "smallest" => "smallest",
            _ => "closest-to-click",
        };
    }

    public bool IsVariantsFallbackOnFailureEnabled()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.fallback-on-failure"));
        return v == null || bool.Parse(v);
    }

    public string GetVariantsEvictionStrategy()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.eviction-strategy"));
        return v switch
        {
            "largest-first" => "largest-first",
            "smallest-first" => "smallest-first",
            "never" => "never",
            _ => "lru",
        };
    }

    public int GetVariantsEvictionActiveGraceSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("variants.eviction-active-grace-seconds"));
        if (v == null) return 60;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 300) : 60;
    }

    public string GetPreflightMode()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.mode"));
        return v switch
        {
            "light" => "light",
            "standard" => "standard",
            "full" => "full",
            _ => "off",
        };
    }

    public int GetPreflightMaxAttempts()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.max-attempts"));
        if (v == null) return 20;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 1, 50) : 20;
    }

    public int GetPreflightTtlSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.ttl-seconds"));
        if (v == null) return 120;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 10, 1800) : 120;
    }

    public int GetPreflightIndexerMaxWaitSeconds()
    {
        var v = StringUtil.EmptyToNull(GetConfigValue("preflight.indexer-max-wait-seconds"));
        if (v == null) return 5;
        return int.TryParse(v, out var n) ? Math.Clamp(n, 0, 120) : 5;
    }

    public bool IsPreviewPar2FilesEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("webdav.preview-par2-files"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsIgnoreSabHistoryLimitEnabled()
    {
        var defaultValue = true;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.ignore-history-limit"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsRepairJobEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("repair.enable"));
        var isRepairJobEnabled = (configValue != null ? bool.Parse(configValue) : defaultValue);
        return isRepairJobEnabled
               && GetLibraryDir() != null
               && GetArrConfig().GetInstanceCount() > 0;
    }

    public ArrConfig GetArrConfig()
    {
        var defaultValue = new ArrConfig();
        return GetConfigValue<ArrConfig>("arr.instances") ?? defaultValue;
    }

    public UsenetProviderConfig GetUsenetProviderConfig()
    {
        var defaultValue = new UsenetProviderConfig();
        return GetConfigValue<UsenetProviderConfig>("usenet.providers") ?? defaultValue;
    }

    public IndexerConfig GetIndexerConfig()
    {
        return GetConfigValue<IndexerConfig>("indexers.instances") ?? new IndexerConfig();
    }

    public ProfileConfig GetProfileConfig()
    {
        return GetConfigValue<ProfileConfig>("profiles.instances") ?? new ProfileConfig();
    }

    public string GetDuplicateNzbBehavior()
    {
        var defaultValue = "increment";
        return GetConfigValue("api.duplicate-nzb-behavior") ?? defaultValue;
    }

    public HashSet<string> GetBlocklistedFiles()
    {
        var defaultValue = "*.nfo, *.par2, *.sfv, *sample.mkv";
        return (GetConfigValue("api.download-file-blocklist") ?? defaultValue)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.ToLower())
            .ToHashSet();
    }

    public string GetImportStrategy()
    {
        return GetConfigValue("api.import-strategy") ?? "symlinks";
    }

    public string GetStrmCompletedDownloadDir()
    {
        return GetConfigValue("api.completed-downloads-dir") ?? "/data/completed-downloads";
    }

    public string GetBaseUrl()
    {
        return GetConfigValue("general.base-url") ?? "http://localhost:3000";
    }

    public bool IsRcloneRemoteControlEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("rclone.rc-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetRcloneHost()
    {
        return GetConfigValue("rclone.host");
    }

    public string? GetRcloneUser()
    {
        return GetConfigValue("rclone.user");
    }

    public string? GetRclonePass()
    {
        return GetConfigValue("rclone.pass");
    }

    public string GetUserAgent()
    {
        var defaultValue = $"nzbdav/{AppVersion}";
        return StringUtil.EmptyToNull(GetConfigValue("api.user-agent"))
               ?? EnvironmentUtil.GetEnvironmentVariable("NZB_GRAB_USER_AGENT")
               ?? defaultValue;
    }

    public bool IsDatabaseStartupVacuumEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("db.is-startup-vacuum-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public bool IsNzbBackupEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public string? GetNzbBackupLocation()
    {
        return StringUtil.EmptyToNull(GetConfigValue("api.nzb-backup-location"));
    }

    public bool IsRemoveOrphanedFilesScheduleEnabled()
    {
        var defaultValue = false;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-enabled"));
        return (configValue != null ? bool.Parse(configValue) : defaultValue);
    }

    public TimeSpan RemoveOrphanedFilesSchedule()
    {
        var defaultValue = TimeSpan.Zero;
        var configValue = StringUtil.EmptyToNull(GetConfigValue("maintenance.remove-orphaned-schedule-time"));
        if (configValue == null) return defaultValue;
        if (!int.TryParse(configValue, out var totalMinutes)) return defaultValue;
        if (totalMinutes < 0 || totalMinutes >= 24 * 60) return defaultValue;
        return TimeSpan.FromMinutes(totalMinutes);
    }

    public class ConfigEventArgs : EventArgs
    {
        public required Dictionary<string, string> ChangedConfig { get; init; }
    }
}