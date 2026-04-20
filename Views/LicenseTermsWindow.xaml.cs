using System.Windows;

namespace RightSpeak.Views;

public partial class LicenseTermsWindow : Window
{
    public LicenseTermsWindow()
    {
        InitializeComponent();
    }

    private void AcceptButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = true;
    }

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        DialogResult = false;
    }
}
