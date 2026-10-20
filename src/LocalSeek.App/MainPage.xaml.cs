using System.ComponentModel;
using LocalSeek.App.ViewModels;
using Microsoft.UI.Xaml.Input;

namespace LocalSeek.App;

public sealed partial class MainPage : Page
{
    public MainPage(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        SearchPage.Initialize(viewModel.Search);
        DiagnosticsPage.Initialize(viewModel.Diagnostics);
        SettingsPage.Initialize(viewModel.Settings);
        NotificationList.ItemsSource = viewModel.Notifications;

        PauseButton.Command = viewModel.Status.TogglePauseCommand;
        viewModel.Status.PropertyChanged += OnStatusChanged;
        UpdateStatus();
    }

    public MainViewModel ViewModel { get; }

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SelectPage(AppPage.Search);
        ViewModel.Search.HandleKey(AppKey.K, AppKeyModifiers.Control);
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is NavigationViewItem { Tag: string tag } && Enum.TryParse<AppPage>(tag, out var page))
        {
            ViewModel.NavigateCommand.Execute(page);
            SearchPage.Visibility = page == AppPage.Search ? Visibility.Visible : Visibility.Collapsed;
            DiagnosticsPage.Visibility = page == AppPage.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
            SettingsPage.Visibility = page == AppPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SelectPage(AppPage page)
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag as string == page.ToString())
            {
                Navigation.SelectedItem = item;
            }
        }
    }

    private void OnNotificationClosed(InfoBar sender, object args) =>
        ViewModel.DismissNotificationCommand.Execute(sender.Message);

    private void OnStatusChanged(object? sender, PropertyChangedEventArgs e) => UpdateStatus();

    private void UpdateStatus()
    {
        var status = ViewModel.Status;
        StatusText.Text = status.Text;
        IndexSummaryText.Text = status.IndexSummary;
        BusyRing.IsActive = status.IsBusy;
        PauseButton.Content = status.PauseButtonText;
    }
}
