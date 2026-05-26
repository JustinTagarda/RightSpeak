using System.Windows;
using System.Windows.Input;

namespace RightSpeak.Views;

public partial class AppStatusDisplay : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ModeTextProperty =
        DependencyProperty.Register(nameof(ModeText), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata("Basic"));

    public static readonly DependencyProperty CanUpgradeProperty =
        DependencyProperty.Register(nameof(CanUpgrade), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty UpgradeTooltipProperty =
        DependencyProperty.Register(nameof(UpgradeTooltip), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UpgradeCommandProperty =
        DependencyProperty.Register(nameof(UpgradeCommand), typeof(ICommand), typeof(AppStatusDisplay), new PropertyMetadata(null));

    public AppStatusDisplay()
    {
        InitializeComponent();
    }

    public string ModeText
    {
        get => (string)GetValue(ModeTextProperty);
        set => SetValue(ModeTextProperty, value);
    }

    public bool CanUpgrade
    {
        get => (bool)GetValue(CanUpgradeProperty);
        set => SetValue(CanUpgradeProperty, value);
    }

    public string UpgradeTooltip
    {
        get => (string)GetValue(UpgradeTooltipProperty);
        set => SetValue(UpgradeTooltipProperty, value);
    }

    public ICommand? UpgradeCommand
    {
        get => (ICommand?)GetValue(UpgradeCommandProperty);
        set => SetValue(UpgradeCommandProperty, value);
    }
}
