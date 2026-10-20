using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LocalSeek.App.ViewModels;

public enum AppPage
{
    Search,
    Diagnostics,
    Settings,
}

public sealed partial class MainViewModel(SearchViewModel search, StatusViewModel status, DiagnosticsViewModel diagnostics, SettingsViewModel settings) : ObservableObject
{
    [ObservableProperty]
    private AppPage _currentPage = AppPage.Search;

    public SearchViewModel Search { get; } = search;

    public StatusViewModel Status { get; } = status;

    public DiagnosticsViewModel Diagnostics { get; } = diagnostics;

    public SettingsViewModel Settings { get; } = settings;

    /// <summary>Messages for the info bar (damaged settings, index repairs, recoveries).</summary>
    public ObservableCollection<string> Notifications { get; } = [];

    [RelayCommand]
    private void Navigate(AppPage page)
    {
        CurrentPage = page;
        if (page == AppPage.Diagnostics)
        {
            Diagnostics.Refresh();
        }
    }

    [RelayCommand]
    private void DismissNotification(string message) => Notifications.Remove(message);

    public void Notify(string message)
    {
        if (!Notifications.Contains(message))
        {
            Notifications.Add(message);
        }
    }

    /// <summary>New data was committed: refresh what is on screen.</summary>
    public void OnIndexChanged()
    {
        Search.Refresh();
        if (CurrentPage == AppPage.Diagnostics)
        {
            Diagnostics.Refresh();
        }
    }
}
