using System.ComponentModel;
using System.Windows;

namespace RightSpeak.Views;

public partial class StoreUpdateProgressWindow : Window
{
    public bool AllowClose { get; set; }

    public StoreUpdateProgressWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }
}
