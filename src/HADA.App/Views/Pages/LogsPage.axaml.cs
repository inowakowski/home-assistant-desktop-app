using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using HADA.Presentation.ViewModels;

namespace HADA.App.Views.Pages;

public sealed partial class LogsPage : UserControl
{
    private MainViewModel? _viewModel;

    public LogsPage() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Follow(DataContext as MainViewModel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Follow(DataContext as MainViewModel);
        ScrollToEndIfEnabled();
    }

    // The page is made anew each time it is shown; the one left behind lets go of the log.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Follow(null);
    }

    private void Follow(MainViewModel? viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.Logs.EntriesAdded -= OnEntriesAdded;
        }

        _viewModel = viewModel;
        if (_viewModel is not null)
        {
            _viewModel.Logs.EntriesAdded += OnEntriesAdded;
        }
    }

    private void OnEntriesAdded(object? sender, EventArgs e) => ScrollToEndIfEnabled();

    private void ScrollToEndIfEnabled()
    {
        if (_viewModel is not { Logs.AutoScroll: true })
        {
            return;
        }

        // After layout, so the new entries are already part of what can be scrolled.
        Dispatcher.UIThread.Post(LogScroll.ScrollToEnd, DispatcherPriority.Background);
    }
}
