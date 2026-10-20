using LocalSeek.App.ViewModels;
using LocalSeek.Core.Index;
using LocalSeek.Indexing.Diagnostics;
using LocalSeek.Indexing.Settings;
using Microsoft.Extensions.Time.Testing;

namespace LocalSeek.Tests.ViewModels;

public sealed class OtherViewModelTests : IDisposable
{
    private static readonly IndexStatistics Stats = new(7, 1234, 3, 5, 4096, 0, 2, 1);
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "localseek-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Status_bar_reflects_indexing_and_toggles_pause()
    {
        var ui = new QueueDispatcher { Immediate = true };
        var controller = new FakeController();
        var vm = new StatusViewModel(ui, () => Stats);
        vm.Attach(controller);

        controller.Set(new Indexing.Pipeline.IndexingStatus(false, true, 42, 1000, 250.4, []));
        Assert.Equal(42, vm.Pending);
        Assert.Contains("索引中", vm.Text, StringComparison.Ordinal);
        Assert.Contains("250 檔/秒", vm.Text, StringComparison.Ordinal);
        Assert.Equal("索引 1,234 份文件，3 個片段", vm.IndexSummary);

        vm.TogglePauseCommand.Execute(null);
        Assert.True(vm.IsPaused);
        Assert.Equal("恢復索引", vm.PauseButtonText);
        vm.TogglePauseCommand.Execute(null);
        Assert.False(vm.IsPaused);
        vm.RescanCommand.Execute(null);
        Assert.Equal(1, controller.Rescans);
    }

    /// <summary>Acceptance #23: the counts on the diagnostics page match the listed entries.</summary>
    [Fact]
    public void Diagnostics_counts_match_the_listed_entries()
    {
        var log = new DiagnosticsLog(new FakeTimeProvider());
        log.Record(@"C:\a.txt", DiagnosticKind.Binary, "b");
        log.Record(@"C:\b.txt", DiagnosticKind.Binary, "b");
        log.Record(@"C:\c.txt", DiagnosticKind.TooLarge, "t");
        log.Record(@"C:\d.txt", DiagnosticKind.LockedRetryLater, "l");
        var vm = new DiagnosticsViewModel(log, () => Stats, TimeZoneInfo.Utc);

        Assert.Equal(4, vm.TotalCount);
        Assert.Equal(4, vm.Items.Count);
        Assert.Equal(vm.Items.Count, vm.Summary.Sum(s => s.Count));
        Assert.Equal(2, vm.Summary.Single(s => s.Kind == DiagnosticKind.Binary).Count);
        Assert.Equal("二進位檔", vm.Summary.Single(s => s.Kind == DiagnosticKind.Binary).Name);
        Assert.Contains("跳過或失敗的檔案：4", vm.Statistics);
        Assert.Contains("索引文件數：1,234", vm.Statistics);

        vm.KindFilter = DiagnosticKind.Binary;
        Assert.Equal(2, vm.Items.Count);

        log.Clear(@"C:\a.txt");
        vm.Refresh();
        Assert.Single(vm.Items);
        Assert.Equal(3, vm.TotalCount);
        Assert.Equal(vm.TotalCount, vm.Summary.Sum(s => s.Count));
    }

    [Fact]
    public void Every_diagnostic_kind_has_a_display_name()
    {
        Assert.All(Enum.GetValues<DiagnosticKind>(), k => Assert.NotEqual(k.ToString(), DiagnosticsViewModel.NameOf(k)));
    }

    [Fact]
    public void Open_report_issues_are_listed()
    {
        var vm = new DiagnosticsViewModel(new DiagnosticsLog(TimeProvider.System), () => Stats, TimeZoneInfo.Utc);

        vm.SetOpenReport(new IndexOpenReport([new IndexIssue(IndexIssueKind.SegmentCorrupt, "seg_1.lss", "片段損毀", 12)]));

        Assert.Equal("片段損毀（影響 12 份文件，將自動重新索引）", Assert.Single(vm.OpenIssues));
    }

    [Fact]
    public void Settings_are_edited_validated_and_saved()
    {
        var store = new SettingsStore(Path.Combine(_directory, "settings.json"));
        var vm = new SettingsViewModel(store, AppSettings.Default, loadWarning: "設定檔損毀");
        AppSettings? saved = null;
        vm.Saved += (_, s) => saved = s;
        Assert.Equal("設定檔損毀", vm.Message);

        vm.NewRoot = @"D:\客戶\";
        vm.AddRootCommand.Execute(null);
        vm.NewRoot = @"d:\客戶";
        vm.AddRootCommand.Execute(null);
        Assert.Contains("已在清單中", vm.Message, StringComparison.Ordinal);
        vm.NewRoot = "relative";
        vm.AddRootCommand.Execute(null);
        Assert.Contains("不是完整路徑", vm.Message, StringComparison.Ordinal);
        vm.ExcludePatternsText = "**/*.tmp\r\n\r\n  build/  ";
        vm.MaxFileMegabytes = 0;
        vm.SaveCommand.Execute(null);
        Assert.Null(saved);
        Assert.Contains("大於 0", vm.Message, StringComparison.Ordinal);

        vm.MaxFileMegabytes = 5;
        vm.SaveCommand.Execute(null);

        Assert.NotNull(saved);
        Assert.Equal([@"D:\客戶"], saved.Roots);
        Assert.Equal(["**/*.tmp", "build/"], saved.ExcludePatterns);
        Assert.Equal(5L * 1024 * 1024, saved.MaxFileBytes);
        Assert.Equal(saved.Roots, store.Load().Settings.Roots);

        vm.RemoveRootCommand.Execute(@"D:\客戶");
        Assert.Empty(vm.Roots);
    }

    [Fact]
    public void Main_view_model_navigates_and_manages_notifications()
    {
        var log = new DiagnosticsLog(TimeProvider.System);
        var main = new MainViewModel(
            new SearchViewModel(new FakeSearchService(), new FakePreviewService(), new FakeShell(), new QueueDispatcher(), new FakeTimeProvider(), TimeZoneInfo.Utc),
            new StatusViewModel(new QueueDispatcher(), () => Stats),
            new DiagnosticsViewModel(log, () => Stats, TimeZoneInfo.Utc),
            new SettingsViewModel(new SettingsStore(Path.Combine(_directory, "s.json")), AppSettings.Default, null));

        log.Record(@"C:\x.txt", DiagnosticKind.Binary, "b");
        main.NavigateCommand.Execute(AppPage.Diagnostics);
        Assert.Equal(AppPage.Diagnostics, main.CurrentPage);
        Assert.Single(main.Diagnostics.Items);

        main.Notify("hello");
        main.Notify("hello");
        Assert.Single(main.Notifications);
        main.DismissNotificationCommand.Execute("hello");
        Assert.Empty(main.Notifications);
    }
}
