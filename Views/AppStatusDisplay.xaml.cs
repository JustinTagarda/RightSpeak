using System.Windows;
using System.Windows.Input;

namespace RightSpeak.Views;

public partial class AppStatusDisplay : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty ModeTextProperty =
        DependencyProperty.Register(nameof(ModeText), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata("Basic"));

    public static readonly DependencyProperty CanUpgradeProperty =
        DependencyProperty.Register(nameof(CanUpgrade), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty IsStatusVisibleProperty =
        DependencyProperty.Register(nameof(IsStatusVisible), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowUpgradeProperty =
        DependencyProperty.Register(nameof(ShowUpgrade), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty UpgradeTooltipProperty =
        DependencyProperty.Register(nameof(UpgradeTooltip), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UpgradeCommandProperty =
        DependencyProperty.Register(nameof(UpgradeCommand), typeof(ICommand), typeof(AppStatusDisplay), new PropertyMetadata(null));

    public static readonly DependencyProperty CanUpdateProperty =
        DependencyProperty.Register(nameof(CanUpdate), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty ShowUpdateProperty =
        DependencyProperty.Register(nameof(ShowUpdate), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty UpdateCommandProperty =
        DependencyProperty.Register(nameof(UpdateCommand), typeof(ICommand), typeof(AppStatusDisplay), new PropertyMetadata(null));

    public static readonly DependencyProperty ShowUpdateProgressProperty =
        DependencyProperty.Register(nameof(ShowUpdateProgress), typeof(bool), typeof(AppStatusDisplay), new PropertyMetadata(false));

    public static readonly DependencyProperty UpdateProgressPercentProperty =
        DependencyProperty.Register(nameof(UpdateProgressPercent), typeof(int), typeof(AppStatusDisplay), new PropertyMetadata(0));

    public static readonly DependencyProperty UpdateProgressPhaseProperty =
        DependencyProperty.Register(nameof(UpdateProgressPhase), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UpdateProgressDetailProperty =
        DependencyProperty.Register(nameof(UpdateProgressDetail), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UpdateProgressResultProperty =
        DependencyProperty.Register(nameof(UpdateProgressResult), typeof(string), typeof(AppStatusDisplay), new PropertyMetadata(string.Empty));

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

    public bool IsStatusVisible
    {
        get => (bool)GetValue(IsStatusVisibleProperty);
        set => SetValue(IsStatusVisibleProperty, value);
    }

    public bool ShowUpgrade
    {
        get => (bool)GetValue(ShowUpgradeProperty);
        set => SetValue(ShowUpgradeProperty, value);
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

    public bool CanUpdate
    {
        get => (bool)GetValue(CanUpdateProperty);
        set => SetValue(CanUpdateProperty, value);
    }

    public bool ShowUpdate
    {
        get => (bool)GetValue(ShowUpdateProperty);
        set => SetValue(ShowUpdateProperty, value);
    }

    public ICommand? UpdateCommand
    {
        get => (ICommand?)GetValue(UpdateCommandProperty);
        set => SetValue(UpdateCommandProperty, value);
    }

    public bool ShowUpdateProgress
    {
        get => (bool)GetValue(ShowUpdateProgressProperty);
        set => SetValue(ShowUpdateProgressProperty, value);
    }

    public int UpdateProgressPercent
    {
        get => (int)GetValue(UpdateProgressPercentProperty);
        set => SetValue(UpdateProgressPercentProperty, value);
    }

    public string UpdateProgressPhase
    {
        get => (string)GetValue(UpdateProgressPhaseProperty);
        set => SetValue(UpdateProgressPhaseProperty, value);
    }

    public string UpdateProgressDetail
    {
        get => (string)GetValue(UpdateProgressDetailProperty);
        set => SetValue(UpdateProgressDetailProperty, value);
    }

    public string UpdateProgressResult
    {
        get => (string)GetValue(UpdateProgressResultProperty);
        set => SetValue(UpdateProgressResultProperty, value);
    }
}
