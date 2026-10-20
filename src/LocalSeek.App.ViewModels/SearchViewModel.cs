using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalSeek.Core.Query;

namespace LocalSeek.App.ViewModels;

/// <summary>
/// Search-as-you-type. Every keystroke bumps a version number, cancels the running search and restarts
/// a 150 ms debounce timer; a search result is applied only if its version is still the latest, so a
/// slow old query can never overwrite the results of a newer one.
/// </summary>
public sealed partial class SearchViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan DebounceDelay = TimeSpan.FromMilliseconds(150);

    private const int PageSize = 10;

    private readonly ISearchService _search;
    private readonly IPreviewService _previewService;
    private readonly IShellService _shell;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;
    private readonly TimeZoneInfo _timeZone;
    private ITimer? _debounce;
    private CancellationTokenSource? _searchCancellation;
    private CancellationTokenSource? _previewCancellation;
    private BoundNode? _activeQuery;
    private long _version;

    [ObservableProperty]
    private string _queryText = "";

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private int _errorPosition;

    [ObservableProperty]
    private int _errorLength;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private int _totalHits;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    [NotifyCanExecuteChangedFor(nameof(RevealSelectedCommand))]
    private ResultItemViewModel? _selectedResult;

    [ObservableProperty]
    private PreviewViewModel? _preview;

    public SearchViewModel(ISearchService search, IPreviewService preview, IShellService shell, IUiDispatcher ui, TimeProvider time, TimeZoneInfo timeZone)
    {
        _search = search;
        _previewService = preview;
        _shell = shell;
        _ui = ui;
        _time = time;
        _timeZone = timeZone;
    }

    /// <summary>The view moves keyboard focus to the search box (Ctrl+K).</summary>
    public event EventHandler? FocusSearchRequested;

    public ObservableCollection<ResultItemViewModel> Results { get; } = [];

    /// <summary>Version of the search whose results are displayed (for tests and diagnostics).</summary>
    public long DisplayedVersion { get; private set; }

    public long CurrentVersion => _version;

    /// <summary>Re-runs the current query immediately (e.g. after the index changed).</summary>
    public void Refresh()
    {
        if (!string.IsNullOrWhiteSpace(QueryText))
        {
            _ = RunSearchAsync(QueryText, ++_version);
        }
    }

    public bool HandleKey(AppKey key, AppKeyModifiers modifiers)
    {
        switch (key)
        {
            case AppKey.K when modifiers == AppKeyModifiers.Control:
                FocusSearch();
                return true;
            case AppKey.Down:
                MoveSelection(1);
                return true;
            case AppKey.Up:
                MoveSelection(-1);
                return true;
            case AppKey.PageDown:
                MoveSelection(PageSize);
                return true;
            case AppKey.PageUp:
                MoveSelection(-PageSize);
                return true;
            case AppKey.Home when modifiers == AppKeyModifiers.Control:
                MoveSelection(int.MinValue / 2);
                return true;
            case AppKey.End when modifiers == AppKeyModifiers.Control:
                MoveSelection(int.MaxValue / 2);
                return true;
            case AppKey.Enter when modifiers == AppKeyModifiers.Alt:
                RevealSelectedCommand.Execute(null);
                return SelectedResult is not null;
            case AppKey.Enter when modifiers == AppKeyModifiers.None:
                OpenSelectedCommand.Execute(null);
                return SelectedResult is not null;
            case AppKey.Escape:
                QueryText = "";
                return true;
            default:
                return false;
        }
    }

    [RelayCommand]
    private void FocusSearch() => FocusSearchRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void MoveSelection(int delta)
    {
        if (Results.Count == 0)
        {
            return;
        }

        var index = SelectedResult is null ? (delta > 0 ? -1 : Results.Count) : Results.IndexOf(SelectedResult);
        var target = (int)Math.Clamp((long)index + delta, 0, Results.Count - 1);
        SelectedResult = Results[target];
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void OpenSelected() => RunShell(_shell.OpenFile, "開啟檔案");

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void RevealSelected() => RunShell(_shell.RevealInExplorer, "開啟所在資料夾");

    private bool HasSelection() => SelectedResult is not null;

    private void RunShell(Action<string> action, string what)
    {
        if (SelectedResult is not { } selected)
        {
            return;
        }

        try
        {
            action(selected.Path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            StatusText = $"無法{what}：{ex.Message}";
        }
    }

    partial void OnQueryTextChanged(string value)
    {
        var version = ++_version;
        _debounce?.Dispose();
        _searchCancellation?.Cancel();
        if (string.IsNullOrWhiteSpace(value))
        {
            IsSearching = false;
            ClearResults();
            ErrorMessage = null;
            StatusText = "";
            DisplayedVersion = version;
            return;
        }

        _debounce = _time.CreateTimer(_ => _ui.Post(() => _ = RunSearchAsync(value, version)), null, DebounceDelay, Timeout.InfiniteTimeSpan);
    }

    partial void OnSelectedResultChanged(ResultItemViewModel? value)
    {
        _previewCancellation?.Cancel();
        Preview = null;
        if (value is null || _activeQuery is null)
        {
            return;
        }

        _ = LoadPreviewAsync(value, _activeQuery);
    }

    private async Task RunSearchAsync(string text, long version)
    {
        if (version != _version)
        {
            return;
        }

        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        IsSearching = true;
        SearchOutcome outcome;
        try
        {
            outcome = await _search.SearchAsync(text, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            if (version == _version)
            {
                IsSearching = false;
            }
        }

        if (version != _version || cancellation.IsCancellationRequested)
        {
            return;
        }

        Apply(outcome);
        DisplayedVersion = version;
    }

    private void Apply(SearchOutcome outcome)
    {
        switch (outcome)
        {
            case SearchOutcome.Invalid invalid:
                ClearResults();
                // Position first: views render the message when it changes.
                ErrorPosition = invalid.Position;
                ErrorLength = invalid.Length;
                ErrorMessage = invalid.Message;
                StatusText = $"查詢有誤：{invalid.Message}（第 {invalid.Position + 1} 個字元）";
                break;

            case SearchOutcome.Success success:
                ErrorMessage = null;
                _activeQuery = success.Query;
                var previouslySelected = SelectedResult?.Path;
                Results.Clear();
                foreach (var hit in success.Hits)
                {
                    Results.Add(new ResultItemViewModel(hit, _timeZone));
                }

                TotalHits = success.TotalHits;
                StatusText = $"共 {success.TotalHits:N0} 筆，顯示前 {success.Hits.Count:N0} 筆（{success.Elapsed.TotalMilliseconds:F0} ms）";
                SelectedResult = Results.FirstOrDefault(r => string.Equals(r.Path, previouslySelected, StringComparison.OrdinalIgnoreCase))
                    ?? Results.FirstOrDefault();
                break;
        }
    }

    private void ClearResults()
    {
        _activeQuery = null;
        Results.Clear();
        TotalHits = 0;
        SelectedResult = null;
    }

    private async Task LoadPreviewAsync(ResultItemViewModel item, BoundNode query)
    {
        var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        try
        {
            var preview = await _previewService.GetPreviewAsync(item.Path, query, cancellation.Token);
            if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedResult, item))
            {
                Preview = new PreviewViewModel(preview);
            }
        }
        catch (OperationCanceledException)
        {
            // Selection moved on.
        }
    }

    public void Dispose()
    {
        _debounce?.Dispose();
        _searchCancellation?.Cancel();
        _previewCancellation?.Cancel();
    }
}
