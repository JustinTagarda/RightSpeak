using System.Windows;
using System.Windows.Input;

namespace RightSpeak.Views;

public partial class ConfirmActionWindow : Window
{
    public ConfirmActionWindow(string title, string message, string confirmText = "Confirm", string cancelText = "Cancel")
    {
        InitializeComponent();
        Title = title;
        MessageTextBlock.Text = message;
        ConfirmButton.Content = confirmText;
        CancelButton.Content = cancelText;
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _ = sender;
        if (e.ClickCount == 2)
        {
            return;
        }

        DragMove();
    }

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
    }

    private void ConfirmButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = true;
    }
}
