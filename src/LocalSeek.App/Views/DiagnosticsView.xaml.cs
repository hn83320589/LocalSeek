using System.ComponentModel;
using LocalSeek.App.ViewModels;

namespace LocalSeek.App.Views;

public sealed partial class DiagnosticsView : UserControl
{
    private DiagnosticsViewModel _viewModel = null!;

    public DiagnosticsView() => InitializeComponent();

    public void Initialize(DiagnosticsViewModel viewModel)
    {
        _viewModel = viewModel;
        StatisticsList.ItemsSource = viewModel.Statistics;
        IssueList.ItemsSource = viewModel.OpenIssues;
        SummaryList.ItemsSource = viewModel.Summary;
        EntryList.ItemsSource = viewModel.Items;
        RefreshButton.Command = viewModel.RefreshCommand;
        viewModel.PropertyChanged += OnViewModelChanged;
        UpdateTitle();
    }

    private void OnSummarySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SummaryList.SelectedItem is DiagnosticKindCount count)
        {
            _viewModel.KindFilter = count.Kind;
        }
    }

    private void OnShowAll(object sender, RoutedEventArgs e)
    {
        SummaryList.SelectedItem = null;
        _viewModel.KindFilter = null;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e) => UpdateTitle();

    private void UpdateTitle() =>
        ListTitle.Text = _viewModel.KindFilter is { } kind
            ? $"{DiagnosticsViewModel.NameOf(kind)}：{_viewModel.Items.Count:N0} 個"
            : $"被跳過或失敗的檔案：{_viewModel.TotalCount:N0} 個";
}
