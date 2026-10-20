using LocalSeek.Indexing.Settings;

namespace LocalSeek.Tests.Settings;

/// <summary>Acceptance #21 (settings part): JSON persistence and recovery from a damaged file.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "localseek-settings-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_directory, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_without_warning()
    {
        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Saved_settings_round_trip()
    {
        var store = new SettingsStore(FilePath);
        var settings = AppSettings.Default with
        {
            Roots = [@"D:\客戶資料", @"\\nas\share"],
            ExcludePatterns = ["**/*.tmp", "build/"],
            MaxFileBytes = 1234567,
            SkipHiddenAndSystem = false,
        };

        store.Save(settings);
        var loaded = store.Load();

        Assert.Null(loaded.Warning);
        Assert.Equal(settings.Roots, loaded.Settings.Roots);
        Assert.Equal(settings.ExcludePatterns, loaded.Settings.ExcludePatterns);
        Assert.Equal(1234567, loaded.Settings.MaxFileBytes);
        Assert.False(loaded.Settings.SkipHiddenAndSystem);
        Assert.Contains("客戶資料", File.ReadAllText(FilePath), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"roots\": 5}")]
    public void Damaged_file_falls_back_to_defaults_with_warning_and_backup(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, content);

        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.Default, result.Settings);
        Assert.NotNull(result.Warning);
        Assert.Contains("設定檔損毀", result.Warning, StringComparison.Ordinal);
        Assert.Equal(content, File.ReadAllText(FilePath + ".bad"));
    }

    [Fact]
    public void Out_of_range_values_are_replaced_by_defaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(FilePath, """{ "maxFileBytes": -5, "debounceMilliseconds": 0, "periodicScanMinutes": -1, "roots": ["C:\\a", "", "C:\\a"] }""");

        var result = new SettingsStore(FilePath).Load();

        Assert.Equal(AppSettings.Default.MaxFileBytes, result.Settings.MaxFileBytes);
        Assert.Equal(AppSettings.Default.DebounceMilliseconds, result.Settings.DebounceMilliseconds);
        Assert.Equal(AppSettings.Default.PeriodicScanMinutes, result.Settings.PeriodicScanMinutes);
        Assert.Equal([@"C:\a"], result.Settings.Roots);
        Assert.Null(result.Warning);
    }

    [Fact]
    public void Default_paths_live_under_local_app_data()
    {
        var paths = AppPaths.Default;

        Assert.EndsWith(Path.Combine("LocalSeek", "settings.json"), paths.SettingsFile, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(Path.Combine("LocalSeek", "index"), paths.IndexDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), paths.Root, StringComparison.OrdinalIgnoreCase);
    }
}
