using System.ComponentModel;
using LocalSeek.App.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace LocalSeek.App.Views;

/// <summary>
/// Thin view over <see cref="SearchViewModel"/>: forwards text and keys, mirrors selection, and renders
/// the preview's highlighted segments as runs. All behaviour is in the (tested) view model.
/// </summary>
public sealed partial class SearchView : UserControl
{
    private SearchViewModel _viewModel = null!;
    private bool _syncingSelection;

    public SearchView() => InitializeComponent();

    public void Initialize(SearchViewModel viewModel)
    {
        _viewModel = viewModel;
        ResultList.ItemsSource = viewModel.Results;
        viewModel.PropertyChanged += OnViewModelChanged;
        viewModel.FocusSearchRequested += (_, _) =>
        {
            SearchBox.Focus(FocusState.Programmatic);
            SearchBox.SelectAll();
        };
        Loaded += (_, _) => SearchBox.Focus(FocusState.Programmatic);
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e) => _viewModel.QueryText = SearchBox.Text;

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var modifiers = AppKeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= AppKeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= AppKeyModifiers.Alt;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= AppKeyModifiers.Shift;
        }

        AppKey? key = e.Key switch
        {
            VirtualKey.Up => AppKey.Up,
            VirtualKey.Down => AppKey.Down,
            VirtualKey.PageUp => AppKey.PageUp,
            VirtualKey.PageDown => AppKey.PageDown,
            VirtualKey.Home when modifiers == AppKeyModifiers.Control => AppKey.Home,
            VirtualKey.End when modifiers == AppKeyModifiers.Control => AppKey.End,
            VirtualKey.Enter => AppKey.Enter,
            VirtualKey.Escape when ReferenceEquals(sender, SearchBox) => AppKey.Escape,
            _ => null,
        };

        if (key is { } k && _viewModel.HandleKey(k, modifiers))
        {
            e.Handled = true;
            if (ReferenceEquals(sender, SearchBox) && k is AppKey.Escape)
            {
                SearchBox.Text = "";
            }
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private void OnResultSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_syncingSelection)
        {
            _viewModel.SelectedResult = ResultList.SelectedItem as ResultItemViewModel;
        }
    }

    private void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        _viewModel.OpenSelectedCommand.Execute(null);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SearchViewModel.SelectedResult):
                _syncingSelection = true;
                ResultList.SelectedItem = _viewModel.SelectedResult;
                _syncingSelection = false;
                if (_viewModel.SelectedResult is { } selected)
                {
                    ResultList.ScrollIntoView(selected);
                }

                break;
            case nameof(SearchViewModel.Preview):
                RenderPreview(_viewModel.Preview);
                break;
            case nameof(SearchViewModel.StatusText):
                StatusText.Text = _viewModel.StatusText;
                break;
            case nameof(SearchViewModel.ErrorMessage):
            case nameof(SearchViewModel.ErrorPosition):
                ErrorText.Text = _viewModel.ErrorMessage is null ? "" : $"查詢語法錯誤（第 {_viewModel.ErrorPosition + 1} 個字元）：{_viewModel.ErrorMessage}";
                ErrorText.Visibility = _viewModel.ErrorMessage is null ? Visibility.Collapsed : Visibility.Visible;
                break;
        }
    }

    private void RenderPreview(PreviewViewModel? preview)
    {
        PreviewPanel.Children.Clear();
        if (preview is null)
        {
            return;
        }

        PreviewPanel.Children.Add(Rich(preview.NameSegments, 18, FontWeights.SemiBold));
        PreviewPanel.Children.Add(new TextBlock
        {
            Text = preview.Path,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });

        if (preview.Message is { } message)
        {
            PreviewPanel.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Informational, Message = message });
        }

        foreach (var snippet in preview.Snippets)
        {
            var block = Rich(snippet.Segments, 14, FontWeights.Normal);
            PreviewPanel.Children.Add(new Border
            {
                Child = block,
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(4),
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            });
        }
    }

    private static RichTextBlock Rich(IReadOnlyList<TextSegment> segments, double fontSize, Windows.UI.Text.FontWeight weight)
    {
        var paragraph = new Paragraph();
        foreach (var segment in segments)
        {
            var run = new Run { Text = segment.Text };
            if (segment.IsHighlight)
            {
                run.FontWeight = FontWeights.Bold;
                run.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
            }

            paragraph.Inlines.Add(run);
        }

        var block = new RichTextBlock { FontSize = fontSize, FontWeight = weight, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        block.Blocks.Add(paragraph);
        return block;
    }
}
