using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalSeek.Core.Index;
using LocalSeek.Indexing.Pipeline;

namespace LocalSeek.App.ViewModels;

/// <summary>Status bar: progress, rate and pending work of background indexing, plus pause/resume.</summary>
public sealed partial class StatusViewModel(IUiDispatcher ui, Func<IndexStatistics> statistics) : ObservableObject
{
    private IIndexingController? _controller;

    [ObservableProperty]
    private bool _isPaused;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private int _pending;

    [ObservableProperty]
    private long _processed;

    [ObservableProperty]
    private string _text = "";

    [ObservableProperty]
    private string _indexSummary = "";

    public string PauseButtonText => IsPaused ? "恢復索引" : "暫停索引";

    /// <summary>Connects to the (re)started indexing service.</summary>
    public void Attach(IIndexingController controller)
    {
        if (_controller is not null)
        {
            _controller.StatusChanged -= OnStatusChanged;
        }

        _controller = controller;
        controller.StatusChanged += OnStatusChanged;
        Update(controller.Status);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_controller is null)
        {
            return;
        }

        if (IsPaused)
        {
            _controller.Resume();
        }
        else
        {
            _controller.Pause();
        }

        Update(_controller.Status);
    }

    [RelayCommand]
    private void Rescan() => _controller?.Rescan();

    public void Update(IndexingStatus status)
    {
        IsPaused = status.Paused;
        IsBusy = status.Busy;
        Pending = status.Pending;
        Processed = status.Processed;
        var state = status.Paused ? "已暫停" : status.Busy || status.Pending > 0 ? "索引中" : "已是最新";
        Text = $"{state}｜已處理 {status.Processed:N0}｜待處理 {status.Pending:N0}｜{status.FilesPerSecond:F0} 檔/秒";
        var stats = statistics();
        IndexSummary = $"索引 {stats.LiveDocuments:N0} 份文件，{stats.SegmentCount} 個片段";
        OnPropertyChanged(nameof(PauseButtonText));
    }

    private void OnStatusChanged(object? sender, IndexingStatus status) => ui.Post(() => Update(status));
}
