using System.Text.Json;
using System.Text.Json.Serialization;

namespace LocalSeek.Indexing.Settings;

/// <summary>User settings persisted as JSON.</summary>
public sealed record AppSettings
{
    public static AppSettings Default { get; } = new();

    public IReadOnlyList<string> Roots { get; init; } = [];

    public IReadOnlyList<string> ExcludePatterns { get; init; } = ["**/.git/", "**/node_modules/"];

    public long MaxFileBytes { get; init; } = 20L * 1024 * 1024;

    public int DebounceMilliseconds { get; init; } = 500;

    public bool SkipHiddenAndSystem { get; init; } = true;

    public bool FollowReparsePoints { get; init; } = true;

    /// <summary>Interval of the fallback difference scan for roots that cannot be watched.</summary>
    public int PeriodicScanMinutes { get; init; } = 5;

    public bool IndexingPaused { get; init; }

    /// <summary>Replaces out-of-range values with defaults and removes blank or duplicate roots.</summary>
    public AppSettings Sanitize() => this with
    {
        Roots = (Roots ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        ExcludePatterns = (ExcludePatterns ?? []).Where(p => p is not null).ToList(),
        MaxFileBytes = MaxFileBytes > 0 ? MaxFileBytes : Default.MaxFileBytes,
        DebounceMilliseconds = DebounceMilliseconds > 0 ? DebounceMilliseconds : Default.DebounceMilliseconds,
        PeriodicScanMinutes = PeriodicScanMinutes > 0 ? PeriodicScanMinutes : Default.PeriodicScanMinutes,
    };
}

public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

/// <summary>Loads and saves <see cref="AppSettings"/>; a damaged file never prevents start-up.</summary>
public sealed class SettingsStore(string filePath)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public string FilePath { get; } = filePath;

    public SettingsLoadResult Load()
    {
        if (!File.Exists(FilePath))
        {
            return new SettingsLoadResult(AppSettings.Default, null);
        }

        string json;
        try
        {
            json = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(AppSettings.Default, $"無法讀取設定檔，暫時使用預設值：{ex.Message}");
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Json)
                ?? throw new JsonException("設定內容是 null");
            return new SettingsLoadResult(settings.Sanitize(), null);
        }
        catch (JsonException ex)
        {
            var backup = FilePath + ".bad";
            File.Copy(FilePath, backup, overwrite: true);
            return new SettingsLoadResult(AppSettings.Default, $"設定檔損毀，已改用預設值（原檔備份為 {Path.GetFileName(backup)}）：{ex.Message}");
        }
    }

    /// <summary>Writes to a temporary file and renames it over the old one, so a crash never leaves half a file.</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Sanitize(), Json));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>Where LocalSeek keeps its data: %LocalAppData%\LocalSeek\.</summary>
public sealed record AppPaths(string Root)
{
    public static AppPaths Default { get; } =
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalSeek"));

    public string SettingsFile => Path.Combine(Root, "settings.json");

    public string IndexDirectory => Path.Combine(Root, "index");
}
