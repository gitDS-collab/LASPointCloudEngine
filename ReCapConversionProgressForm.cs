using System;
using System.Globalization;
using WinForms = System.Windows.Forms;

namespace LASPointCloudEngine;

/// <summary>
/// A modeless monitor for a DeCap conversion. Autodesk's documented DeCap interface does not
/// expose a reliable completed-point percentage, so the bar deliberately remains animated until
/// DeCap supplies a real percentage or the indexed dataset is verified complete.
/// </summary>
internal sealed class ReCapConversionProgressForm : WinForms.Form
{
    private readonly Func<ReCapConversionSnapshot?> _snapshotProvider;
    private readonly WinForms.Timer _refreshTimer;
    private readonly WinForms.ProgressBar _progressBar;
    private readonly WinForms.Label _stateLabel;
    private readonly WinForms.Label _detailLabel;
    private readonly WinForms.Label _metricsLabel;

    public ReCapConversionProgressForm(Func<ReCapConversionSnapshot?> snapshotProvider)
    {
        _snapshotProvider = snapshotProvider;
        Text = "ReCap LAS Indexing Progress";
        Width = 610;
        Height = 305;
        FormBorderStyle = WinForms.FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = true;
        ShowInTaskbar = true;
        StartPosition = WinForms.FormStartPosition.CenterScreen;

        _stateLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 18,
            Top = 16,
            Width = 560,
            Height = 28,
            Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold),
            Text = "Preparing ReCap conversion",
        };
        _progressBar = new WinForms.ProgressBar
        {
            Left = 18,
            Top = 52,
            Width = 560,
            Height = 24,
            Style = WinForms.ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
        };
        _detailLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 18,
            Top = 91,
            Width = 560,
            Height = 62,
            AutoEllipsis = true,
            Text = "ReCap does not expose a reliable percentage for LAS indexing. The animated bar confirms that the background job is active.",
        };
        _metricsLabel = new WinForms.Label
        {
            AutoSize = false,
            Left = 18,
            Top = 160,
            Width = 560,
            Height = 46,
            AutoEllipsis = true,
            Text = "Elapsed: starting   |   Waiting for ReCap activity.",
        };
        WinForms.Button closeButton = new()
        {
            Text = "Hide",
            Left = 478,
            Top = 224,
            Width = 100,
        };
        closeButton.Click += (_, _) => Close();

        Controls.AddRange(new WinForms.Control[]
        {
            _stateLabel,
            _progressBar,
            _detailLabel,
            _metricsLabel,
            closeButton,
        });

        _refreshTimer = new WinForms.Timer { Interval = 750 };
        _refreshTimer.Tick += (_, _) => RefreshStatus();
        FormClosed += (_, _) => _refreshTimer.Dispose();
        RefreshStatus();
        _refreshTimer.Start();
    }

    private void RefreshStatus()
    {
        ReCapConversionSnapshot? snapshot = _snapshotProvider();
        if (snapshot is null)
        {
            _stateLabel.Text = "No conversion is active in this Revit session.";
            _detailLabel.Text = "Start Create Indexed RCP from LAS to begin a monitored ReCap conversion.";
            SetIndeterminate();
            _refreshTimer.Stop();
            return;
        }

        _stateLabel.Text = snapshot.State;
        _detailLabel.Text = snapshot.Detail;

        if (snapshot.IsReady)
            SetDeterminate(100);
        else if (snapshot.NativeProgressPercent is int percentage)
            SetDeterminate(Math.Min(percentage, 99));
        else
            SetIndeterminate();

        _metricsLabel.Text = BuildActivityText(snapshot);
        if (snapshot.IsFinished)
            _refreshTimer.Stop();
    }

    private void SetIndeterminate()
    {
        if (_progressBar.Style != WinForms.ProgressBarStyle.Marquee)
        {
            _progressBar.Style = WinForms.ProgressBarStyle.Marquee;
            _progressBar.MarqueeAnimationSpeed = 30;
        }
    }

    private void SetDeterminate(int percentage)
    {
        int boundedPercentage = Math.Clamp(percentage, 0, 100);
        if (_progressBar.Style != WinForms.ProgressBarStyle.Blocks)
            _progressBar.Style = WinForms.ProgressBarStyle.Blocks;
        _progressBar.Value = boundedPercentage;
    }

    private static string BuildActivityText(ReCapConversionSnapshot snapshot)
    {
        TimeSpan? elapsed = snapshot.StartedUtc is null ? null : DateTimeOffset.UtcNow - snapshot.StartedUtc.Value;
        string elapsedText = elapsed is null ? "Elapsed: starting" : "Elapsed: " + FormatElapsed(elapsed.Value);
        if (string.IsNullOrWhiteSpace(snapshot.LastConverterMessage))
            return elapsedText + "   |   Waiting for ReCap activity.";

        const int maximumLength = 210;
        string message = snapshot.LastConverterMessage.Trim();
        if (message.Length > maximumLength)
            message = message[^maximumLength..];
        return elapsedText + "   |   ReCap: " + message;
    }

    private static string FormatElapsed(TimeSpan value)
    {
        return value.TotalHours >= 1
            ? value.ToString("h\\:mm\\:ss", CultureInfo.InvariantCulture)
            : value.ToString("m\\:ss", CultureInfo.InvariantCulture);
    }

}

internal sealed class RevitWindowHandle : WinForms.IWin32Window
{
    public RevitWindowHandle(IntPtr handle)
    {
        Handle = handle;
    }

    public IntPtr Handle { get; }
}
