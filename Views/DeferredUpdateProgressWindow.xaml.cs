using System;
using System.ComponentModel;
using System.Windows;
using RightSpeak.Models;

namespace RightSpeak.Views;

public partial class DeferredUpdateProgressWindow : Window
{
    private bool _allowClose;

    public DeferredUpdateProgressWindow()
    {
        InitializeComponent();
    }

    public void SetStatus(string statusText)
    {
        StatusTextBlock.Text = string.IsNullOrWhiteSpace(statusText)
            ? "Applying update..."
            : statusText;
    }

    public void SetProgress(double? progress)
    {
        if (progress is null)
        {
            UpdateProgressBar.IsIndeterminate = true;
            UpdateProgressBar.Value = 0d;
            return;
        }

        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = Math.Max(0d, Math.Min(100d, progress.Value));
    }

    public void SetSnapshot(AppUpdateSnapshot snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        SetStatus(snapshot.StageText);
        DetailTextBlock.Text = string.IsNullOrWhiteSpace(snapshot.StatusMessage)
            ? DetailTextBlock.Text
            : snapshot.StatusMessage;
        SetProgress(snapshot.IsProgressVisible ? snapshot.ProgressValue * 100d : null);
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _ = sender;

        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        _allowClose = true;
        base.OnClosed(e);
    }

    public new void Close()
    {
        _allowClose = true;
        base.Close();
    }
}
