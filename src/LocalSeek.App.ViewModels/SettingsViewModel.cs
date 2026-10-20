using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalSeek.Indexing.Settings;

namespace LocalSeek.App.ViewModels;

/// <summary>Edits <see cref="AppSettings"/>; saving persists them and raises <see cref="Saved"/>.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private AppSettings _settings;

    [ObservableProperty]
    private string _newRoot = "";

    [ObservableProperty]
    private string? _selectedRoot;

    [ObservableProperty]
    private string _excludePatternsText;

    [ObservableProperty]
    private double _maxFileMegabytes;

    [ObservableProperty]
    private bool _skipHiddenAndSystem;

    [ObservableProperty]
    private bool _followReparsePoints;

    [ObservableProperty]
    private double _periodicScanMinutes;

    [ObservableProperty]
    private string? _message;

    public SettingsViewModel(SettingsStore store, AppSettings settings, string? loadWarning)
    {
        _store = store;
        _settings = settings;
        Roots = new ObservableCollection<string>(settings.Roots);
        _excludePatternsText = string.Join(Environment.NewLine, settings.ExcludePatterns);
        _maxFileMegabytes = settings.MaxFileBytes / (1024.0 * 1024);
        _skipHiddenAndSystem = settings.SkipHiddenAndSystem;
        _followReparsePoints = settings.FollowReparsePoints;
        _periodicScanMinutes = settings.PeriodicScanMinutes;
        _message = loadWarning;
    }

    public event EventHandler<AppSettings>? Saved;

    public ObservableCollection<string> Roots { get; }

    public AppSettings Current => _settings;

    [RelayCommand]
    private void AddRoot()
    {
        var root = NewRoot.Trim().TrimEnd('\\', '/');
        if (root.Length == 0)
        {
            Message = "請輸入或選擇資料夾";
            return;
        }

        if (!Path.IsPathFullyQualified(root + '\\'))
        {
            Message = $"「{root}」不是完整路徑";
            return;
        }

        if (Roots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            Message = $"「{root}」已在清單中";
            return;
        }

        Roots.Add(root);
        NewRoot = "";
        Message = null;
    }

    [RelayCommand]
    private void RemoveRoot(string? root)
    {
        root ??= SelectedRoot;
        if (root is not null)
        {
            Roots.Remove(root);
        }
    }

    [RelayCommand]
    private void Save()
    {
        if (MaxFileMegabytes <= 0 || double.IsNaN(MaxFileMegabytes))
        {
            Message = "檔案大小上限必須大於 0";
            return;
        }

        if (PeriodicScanMinutes < 1 || double.IsNaN(PeriodicScanMinutes))
        {
            Message = "週期掃描間隔至少 1 分鐘";
            return;
        }

        _settings = (_settings with
        {
            Roots = [.. Roots],
            ExcludePatterns = ExcludePatternsText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            MaxFileBytes = (long)(MaxFileMegabytes * 1024 * 1024),
            SkipHiddenAndSystem = SkipHiddenAndSystem,
            FollowReparsePoints = FollowReparsePoints,
            PeriodicScanMinutes = (int)PeriodicScanMinutes,
        }).Sanitize();

        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = $"無法儲存設定：{ex.Message}";
            return;
        }

        Message = "設定已儲存，正在依新設定重新索引";
        Saved?.Invoke(this, _settings);
    }
}
