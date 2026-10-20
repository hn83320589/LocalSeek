using LocalSeek.App.ViewModels;
using Microsoft.Extensions.Time.Testing;

namespace LocalSeek.Tests.ViewModels;

/// <summary>Acceptance #20 with a fake clock and a fake dispatcher.</summary>
public sealed class SearchViewModelTests : IDisposable
{
    private static readonly TimeZoneInfo Utc8 = TimeZoneInfo.CreateCustomTimeZone("UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");

    private readonly FakeTimeProvider _time = new();
    private readonly QueueDispatcher _ui = new();
    private readonly FakeSearchService _search = new();
    private readonly FakePreviewService _preview = new();
    private readonly FakeShell _shell = new();
    private readonly SearchViewModel _vm;

    public SearchViewModelTests() => _vm = new SearchViewModel(_search, _preview, _shell, _ui, _time, Utc8);

    public void Dispose() => _vm.Dispose();

    /// <summary>Types text and lets the debounce elapse and the UI thread run.</summary>
    private FakeSearchService.Call Type(string text)
    {
        _vm.QueryText = text;
        _time.Advance(SearchViewModel.DebounceDelay);
        _ui.RunPending();
        return _search.Calls[^1];
    }

    [Fact]
    public void Search_starts_only_after_150_ms_without_typing()
    {
        _vm.QueryText = "a";
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _vm.QueryText = "ap";
        _time.Advance(TimeSpan.FromMilliseconds(100));
        _ui.RunPending();
        Assert.Empty(_search.Calls);

        _time.Advance(TimeSpan.FromMilliseconds(50));
        _ui.RunPending();

        Assert.Equal("ap", Assert.Single(_search.Calls).Query);
    }

    [Fact]
    public void Debounced_search_runs_on_the_ui_dispatcher()
    {
        _vm.QueryText = "apple";
        _time.Advance(SearchViewModel.DebounceDelay);

        Assert.Empty(_search.Calls);
        Assert.Equal(1, _ui.Pending);
        _ui.RunPending();
        Assert.Single(_search.Calls);
    }

    [Fact]
    public void Typing_cancels_the_running_search_immediately()
    {
        var first = Type("apple");
        Assert.True(_vm.IsSearching);

        _vm.QueryText = "apple pie";

        Assert.True(first.Token.IsCancellationRequested);
        Assert.Single(_search.Calls);
    }

    [Fact]
    public void Results_of_an_older_query_never_overwrite_newer_results()
    {
        var older = Type("apple");
        var newer = Type("banana");

        newer.Complete(@"C:\banana.txt");
        older.Complete(@"C:\apple1.txt", @"C:\apple2.txt");

        Assert.Equal([@"C:\banana.txt"], _vm.Results.Select(r => r.Path));
        Assert.Equal(1, _vm.TotalHits);
        Assert.Equal(_vm.CurrentVersion, _vm.DisplayedVersion);
        Assert.False(_vm.IsSearching);
    }

    [Fact]
    public void Cancelled_search_that_throws_leaves_state_untouched()
    {
        var older = Type("apple");
        var newer = Type("banana");
        newer.Complete(@"C:\b.txt");

        older.Completion.SetCanceled(older.Token);

        Assert.Equal([@"C:\b.txt"], _vm.Results.Select(r => r.Path));
    }

    [Fact]
    public void Results_are_shown_with_metadata_and_first_one_selected()
    {
        Type("apple").Complete(@"C:\docs\a.txt", @"C:\docs\b.txt");

        Assert.Equal(2, _vm.Results.Count);
        var first = _vm.Results[0];
        Assert.Same(first, _vm.SelectedResult);
        Assert.Equal("a.txt", first.FileName);
        Assert.Equal(@"C:\docs", first.Directory);
        Assert.Equal("2.0 KB", first.SizeText);
        Assert.Equal("2024-01-02 11:04", first.ModifiedText);
        Assert.Contains("共 2 筆", _vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Syntax_error_is_reported_with_position_and_clears_results()
    {
        Type("apple").Complete(@"C:\a.txt");

        Type("a AND").Completion.SetResult(new SearchOutcome.Invalid("AND 後面缺少條件", 2, 3));

        Assert.Equal("AND 後面缺少條件", _vm.ErrorMessage);
        Assert.Equal((2, 3), (_vm.ErrorPosition, _vm.ErrorLength));
        Assert.Empty(_vm.Results);

        Type("apple").Complete(@"C:\a.txt");
        Assert.Null(_vm.ErrorMessage);
    }

    [Fact]
    public void Clearing_the_query_clears_results_without_searching()
    {
        Type("apple").Complete(@"C:\a.txt");
        _vm.QueryText = "   ";
        _time.Advance(TimeSpan.FromSeconds(1));
        _ui.RunPending();

        Assert.Single(_search.Calls);
        Assert.Empty(_vm.Results);
        Assert.Null(_vm.SelectedResult);
    }

    [Fact]
    public void Refresh_keeps_the_selected_document_when_it_is_still_found()
    {
        Type("apple").Complete(@"C:\a.txt", @"C:\b.txt");
        _vm.SelectedResult = _vm.Results[1];

        _vm.Refresh();
        _search.Calls[^1].Complete(@"C:\new.txt", @"C:\a.txt", @"C:\b.txt");

        Assert.Equal(@"C:\b.txt", _vm.SelectedResult!.Path);
    }

    // ------------------------------------------------------------------ preview

    [Fact]
    public void Selecting_a_result_loads_its_preview_and_ignores_stale_previews()
    {
        Type("apple").Complete(@"C:\a.txt", @"C:\b.txt");
        var first = _preview.Calls[^1];
        _vm.SelectedResult = _vm.Results[1];
        var second = _preview.Calls[^1];

        Assert.True(first.Token.IsCancellationRequested);
        second.Completion.SetResult(FakePreviewService.For(@"C:\b.txt", "ap text"));
        first.Completion.SetResult(FakePreviewService.For(@"C:\a.txt", "old"));

        Assert.Equal(@"C:\b.txt", _vm.Preview!.Path);
        var segments = _vm.Preview.Snippets.Single().Segments;
        Assert.Equal([new TextSegment("ap", true), new TextSegment(" text", false)], segments);
    }

    // ------------------------------------------------------------------ keyboard

    [Fact]
    public void Ctrl_k_requests_focus_on_the_search_box()
    {
        var focused = 0;
        _vm.FocusSearchRequested += (_, _) => focused++;

        Assert.True(_vm.HandleKey(AppKey.K, AppKeyModifiers.Control));
        Assert.False(_vm.HandleKey(AppKey.K, AppKeyModifiers.None));
        Assert.Equal(1, focused);
    }

    [Fact]
    public void Arrow_keys_move_the_selection_within_bounds()
    {
        Type("apple").Complete(Enumerable.Range(0, 25).Select(i => $@"C:\{i:D2}.txt").ToArray());

        _vm.HandleKey(AppKey.Down, AppKeyModifiers.None);
        Assert.Equal(@"C:\01.txt", _vm.SelectedResult!.Path);
        _vm.HandleKey(AppKey.Up, AppKeyModifiers.None);
        _vm.HandleKey(AppKey.Up, AppKeyModifiers.None);
        Assert.Equal(@"C:\00.txt", _vm.SelectedResult!.Path);
        _vm.HandleKey(AppKey.PageDown, AppKeyModifiers.None);
        Assert.Equal(@"C:\10.txt", _vm.SelectedResult!.Path);
        _vm.HandleKey(AppKey.End, AppKeyModifiers.Control);
        Assert.Equal(@"C:\24.txt", _vm.SelectedResult!.Path);
        _vm.HandleKey(AppKey.Down, AppKeyModifiers.None);
        Assert.Equal(@"C:\24.txt", _vm.SelectedResult!.Path);
        _vm.HandleKey(AppKey.Home, AppKeyModifiers.Control);
        Assert.Equal(@"C:\00.txt", _vm.SelectedResult!.Path);
    }

    [Fact]
    public void Enter_opens_and_alt_enter_reveals_the_selected_file()
    {
        Type("apple").Complete(@"C:\a.txt", @"C:\b.txt");
        _vm.HandleKey(AppKey.Down, AppKeyModifiers.None);

        Assert.True(_vm.HandleKey(AppKey.Enter, AppKeyModifiers.None));
        Assert.True(_vm.HandleKey(AppKey.Enter, AppKeyModifiers.Alt));

        Assert.Equal([@"C:\b.txt"], _shell.Opened);
        Assert.Equal([@"C:\b.txt"], _shell.Revealed);
    }

    [Fact]
    public void Enter_without_selection_does_nothing()
    {
        Assert.False(_vm.HandleKey(AppKey.Enter, AppKeyModifiers.None));
        Assert.False(_vm.OpenSelectedCommand.CanExecute(null));
        Assert.Empty(_shell.Opened);
    }

    [Fact]
    public void Failure_to_open_is_reported_not_thrown()
    {
        Type("apple").Complete(@"C:\a.txt");
        _shell.FailWith = new System.ComponentModel.Win32Exception("沒有關聯的應用程式");

        _vm.HandleKey(AppKey.Enter, AppKeyModifiers.None);

        Assert.Contains("無法開啟檔案", _vm.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void Escape_clears_the_query()
    {
        Type("apple").Complete(@"C:\a.txt");

        Assert.True(_vm.HandleKey(AppKey.Escape, AppKeyModifiers.None));

        Assert.Equal("", _vm.QueryText);
        Assert.Empty(_vm.Results);
    }
}
