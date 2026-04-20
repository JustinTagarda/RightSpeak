using System.Windows;
using System.Windows.Input;
using RightSpeak.ViewModels;

namespace RightSpeak.Views;

public partial class VoiceManagerWindow : Window
{
    private readonly VoiceManagerViewModel _viewModel;

    public VoiceManagerWindow(VoiceManagerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.LoadAsync().ConfigureAwait(true);
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        DragMove();
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        ToggleMaximizeRestore();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        Close();
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
