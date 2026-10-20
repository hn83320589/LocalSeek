using System.ComponentModel;
using LocalSeek.App.ViewModels;
using Windows.Storage.Pickers;

namespace LocalSeek.App.Views;

public sealed partial class SettingsView : UserControl
{
    private SettingsViewModel _viewModel = null!;

    public SettingsView() => InitializeComponent();

    /// <summary>Window handle for pickers in an unpackaged app.</summary>
    public static nint WindowHandle { get; set; }

    public void Initialize(SettingsViewModel viewModel)
    {
        _viewModel = viewModel;
        RootList.ItemsSource = viewModel.Roots;
        ExcludeBox.Text = viewModel.ExcludePatternsText;
        MaxSizeBox.Value = viewModel.MaxFileMegabytes;
        PeriodicBox.Value = viewModel.PeriodicScanMinutes;
        HiddenSwitch.IsOn = viewModel.SkipHiddenAndSystem;
        LinksSwitch.IsOn = viewModel.FollowReparsePoints;
        AddRootButton.Command = viewModel.AddRootCommand;
        SaveButton.Command = viewModel.SaveCommand;
        MessageText.Text = viewModel.Message ?? "";
        viewModel.PropertyChanged += OnViewModelChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.Message):
                MessageText.Text = _viewModel.Message ?? "";
                break;
            case nameof(SettingsViewModel.NewRoot) when NewRootBox.Text != _viewModel.NewRoot:
                NewRootBox.Text = _viewModel.NewRoot;
                break;
        }
    }

    private void OnNewRootChanged(object sender, TextChangedEventArgs e) => _viewModel.NewRoot = NewRootBox.Text;

    private void OnExcludeChanged(object sender, TextChangedEventArgs e) => _viewModel.ExcludePatternsText = ExcludeBox.Text;

    private void OnMaxSizeChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => _viewModel.MaxFileMegabytes = sender.Value;

    private void OnPeriodicChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => _viewModel.PeriodicScanMinutes = sender.Value;

    private void OnHiddenToggled(object sender, RoutedEventArgs e) => _viewModel.SkipHiddenAndSystem = HiddenSwitch.IsOn;

    private void OnLinksToggled(object sender, RoutedEventArgs e) => _viewModel.FollowReparsePoints = LinksSwitch.IsOn;

    private void OnRemoveRoot(object sender, RoutedEventArgs e) => _viewModel.RemoveRootCommand.Execute((sender as FrameworkElement)?.Tag as string);

    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            _viewModel.NewRoot = folder.Path;
            _viewModel.AddRootCommand.Execute(null);
        }
    }
}
