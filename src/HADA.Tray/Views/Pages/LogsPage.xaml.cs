using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HADA.Tray.ViewModels;

namespace HADA.Tray.Views.Pages;

public partial class LogsPage : Page
{
    public LogsPage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => ScrollToEndIfEnabled();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is MainViewModel previous)
        {
            previous.Logs.EntriesAdded -= OnEntriesAdded;
        }

        if (e.NewValue is MainViewModel current)
        {
            current.Logs.EntriesAdded += OnEntriesAdded;
        }
    }

    private void OnEntriesAdded(object? sender, EventArgs e) => ScrollToEndIfEnabled();

    private void ScrollToEndIfEnabled()
    {
        if (DataContext is not MainViewModel { Logs.AutoScroll: true })
        {
            return;
        }

        // Deferred until after layout, so the new entries are already part of the scrollable extent.
        Dispatcher.InvokeAsync(
            () => (LogList.Template?.FindName("LogScroll", LogList) as ScrollViewer)?.ScrollToEnd(),
            DispatcherPriority.Background);
    }
}
