using System.Diagnostics;
using LocalSeek.App.ViewModels;
using Microsoft.UI.Dispatching;

namespace LocalSeek.App.Services;

/// <summary>Posts work to the window's UI thread.</summary>
public sealed class DispatcherQueueDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (!queue.TryEnqueue(() => action()))
        {
            throw new InvalidOperationException("The UI thread is shutting down.");
        }
    }
}

/// <summary>Opens files with their default program and shows them in File Explorer.</summary>
public sealed class ShellService : IShellService
{
    public void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();

    public void RevealInExplorer(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
}
