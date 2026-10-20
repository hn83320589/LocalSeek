using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalSeek.Core.Index;
using LocalSeek.Indexing.Diagnostics;

namespace LocalSeek.App.ViewModels;

public sealed record DiagnosticItem(string Path, string Kind, string Message, string Time);

public sealed record DiagnosticKindCount(DiagnosticKind Kind, string Name, int Count);

/// <summary>Index statistics and the skipped/failed files with their reasons. Counts are taken from the same snapshot of the log as the list.</summary>
public sealed partial class DiagnosticsViewModel : ObservableObject
{
    private readonly DiagnosticsLog _log;
    private readonly Func<IndexStatistics> _statistics;
    private readonly TimeZoneInfo _timeZone;
    private IReadOnlyList<DiagnosticEntry> _entries = [];

    [ObservableProperty]
    private DiagnosticKind? _kindFilter;

    [ObservableProperty]
    private int _totalCount;

    public DiagnosticsViewModel(DiagnosticsLog log, Func<IndexStatistics> statistics, TimeZoneInfo timeZone)
    {
        _log = log;
        _statistics = statistics;
        _timeZone = timeZone;
        Refresh();
    }

    public ObservableCollection<DiagnosticItem> Items { get; } = [];

    public ObservableCollection<DiagnosticKindCount> Summary { get; } = [];

    public ObservableCollection<string> Statistics { get; } = [];

    public ObservableCollection<string> OpenIssues { get; } = [];

    public static string NameOf(DiagnosticKind kind) => kind switch
    {
        DiagnosticKind.TooLarge => "超過大小上限",
        DiagnosticKind.Binary => "二進位檔",
        DiagnosticKind.UnknownEncoding => "無法判定編碼",
        DiagnosticKind.Corrupt => "檔案損毀",
        DiagnosticKind.AccessDenied => "權限不足",
        DiagnosticKind.LockedRetryLater => "被鎖定（稍後重試）",
        DiagnosticKind.LockedGaveUp => "持續被鎖定",
        DiagnosticKind.ChangedDuringRead => "讀取中被修改",
        DiagnosticKind.ReadError => "讀取錯誤",
        DiagnosticKind.LoopDetected => "迴圈或重複連結",
        DiagnosticKind.BrokenLink => "連結失效",
        DiagnosticKind.ReparsePointSkipped => "未跟隨連結",
        DiagnosticKind.HiddenOrSystem => "隱藏或系統檔",
        DiagnosticKind.RootUnavailable => "根資料夾無法使用",
        DiagnosticKind.WatcherFallback => "改用週期掃描",
        _ => kind.ToString(),
    };

    public void SetOpenReport(IndexOpenReport report)
    {
        OpenIssues.Clear();
        foreach (var issue in report.Issues)
        {
            OpenIssues.Add(issue.AffectedDocuments > 0 ? $"{issue.Message}（影響 {issue.AffectedDocuments} 份文件，將自動重新索引）" : issue.Message);
        }
    }

    [RelayCommand]
    public void Refresh()
    {
        _entries = _log.Entries();
        TotalCount = _entries.Count;

        Summary.Clear();
        foreach (var group in _entries.GroupBy(e => e.Kind).OrderBy(g => g.Key))
        {
            Summary.Add(new DiagnosticKindCount(group.Key, NameOf(group.Key), group.Count()));
        }

        var stats = _statistics();
        Statistics.Clear();
        Statistics.Add($"索引文件數：{stats.LiveDocuments:N0}");
        Statistics.Add($"片段數：{stats.SegmentCount}");
        Statistics.Add($"已刪除待合併：{stats.DeletedDocuments:N0}");
        Statistics.Add($"索引大小：{Format.Size(stats.IndexBytes)}");
        Statistics.Add($"提交版本：{stats.Generation}");
        Statistics.Add($"完成合併：{stats.MergesCompleted}（放棄 {stats.MergesAborted}）");
        Statistics.Add($"跳過或失敗的檔案：{_entries.Count:N0}");
        ApplyFilter();
    }

    partial void OnKindFilterChanged(DiagnosticKind? value) => ApplyFilter();

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var entry in _entries.Where(e => KindFilter is null || e.Kind == KindFilter))
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(entry.TimeUtc, _timeZone);
            Items.Add(new DiagnosticItem(entry.Path, NameOf(entry.Kind), entry.Message, local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
        }
    }
}
