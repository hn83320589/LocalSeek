using LocalSeek.App.ViewModels;
using LocalSeek.Core.Query;
using LocalSeek.Core.Search;
using LocalSeek.Indexing.Pipeline;
using LocalSeek.Indexing.Preview;

namespace LocalSeek.Tests.ViewModels;

/// <summary>Collects posted actions; the test decides when the "UI thread" runs them.</summary>
public sealed class QueueDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _queue = new();

    public bool Immediate { get; set; }

    public int Pending => _queue.Count;

    public void Post(Action action)
    {
        if (Immediate)
        {
            action();
        }
        else
        {
            lock (_queue)
            {
                _queue.Enqueue(action);
            }
        }
    }

    public void RunPending()
    {
        while (true)
        {
            Action action;
            lock (_queue)
            {
                if (!_queue.TryDequeue(out action!))
                {
                    return;
                }
            }

            action();
        }
    }
}

public sealed class FakeShell : IShellService
{
    public List<string> Opened { get; } = [];

    public List<string> Revealed { get; } = [];

    public Exception? FailWith { get; set; }

    public void OpenFile(string path)
    {
        if (FailWith is not null)
        {
            throw FailWith;
        }

        Opened.Add(path);
    }

    public void RevealInExplorer(string path) => Revealed.Add(path);
}

/// <summary>Each search returns a task the test completes explicitly.</summary>
public sealed class FakeSearchService : ISearchService
{
    public List<Call> Calls { get; } = [];

    public Task<SearchOutcome> SearchAsync(string query, CancellationToken cancellation)
    {
        var call = new Call(query, cancellation, new TaskCompletionSource<SearchOutcome>());
        Calls.Add(call);
        return call.Completion.Task;
    }

    public static SearchOutcome Results(string query, params string[] paths) =>
        new SearchOutcome.Success(
            QueryBinder.Bind(QueryParser.Parse(query)),
            paths.Select((p, i) => new SearchResultItem(p, 10 - i, 2048, new DateTime(2024, 1, 2, 3, 4, 0, DateTimeKind.Utc))).ToList(),
            paths.Length,
            TimeSpan.FromMilliseconds(5));

    public sealed record Call(string Query, CancellationToken Token, TaskCompletionSource<SearchOutcome> Completion)
    {
        public void Complete(params string[] paths) => Completion.SetResult(Results(Query, paths));
    }
}

public sealed class FakePreviewService : IPreviewService
{
    public List<(string Path, TaskCompletionSource<DocumentPreview> Completion, CancellationToken Token)> Calls { get; } = [];

    public Task<DocumentPreview> GetPreviewAsync(string path, BoundNode query, CancellationToken cancellation)
    {
        var tcs = new TaskCompletionSource<DocumentPreview>();
        Calls.Add((path, tcs, cancellation));
        return tcs.Task;
    }

    public static DocumentPreview For(string path, string text) =>
        new(path, [new Snippet(0, text, [new Highlight(0, 2)])], [], null);
}

public sealed class FakeController : IIndexingController
{
    public event EventHandler<IndexingStatus>? StatusChanged;

    public IndexingStatus Status { get; private set; } = new(false, false, 0, 0, 0, []);

    public int Rescans { get; private set; }

    public void Pause() => Set(Status with { Paused = true });

    public void Resume() => Set(Status with { Paused = false });

    public void Rescan() => Rescans++;

    public void Set(IndexingStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, status);
    }
}
