using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Steamy.Models;

namespace Steamy.Controls;

// A bounded history of real transfer samples. Painting one visual avoids a tree
// of animated bars and stops entirely when the download page leaves the screen.
public sealed class DownloadRateGraph : FrameworkElement
{
    public static readonly DependencyProperty JobProperty = DependencyProperty.Register(
        nameof(Job), typeof(DownloadJob), typeof(DownloadRateGraph),
        new FrameworkPropertyMetadata(null, (sender, _) => ((DownloadRateGraph)sender).ResetHistory()));
    private static readonly DependencyPropertyKey PeakDisplayPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PeakDisplay), typeof(string), typeof(DownloadRateGraph), new PropertyMetadata("—"));
    public static readonly DependencyProperty PeakDisplayProperty = PeakDisplayPropertyKey.DependencyProperty;
    private static readonly DependencyPropertyKey PeakBitrateDisplayPropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(PeakBitrateDisplay), typeof(string), typeof(DownloadRateGraph), new PropertyMetadata("—"));
    public static readonly DependencyProperty PeakBitrateDisplayProperty = PeakBitrateDisplayPropertyKey.DependencyProperty;
    private readonly Queue<double> _samples = new();
    private readonly DispatcherTimer _timer;
    private DownloadJob? _sampledJob;
    public DownloadJob? Job { get => (DownloadJob?)GetValue(JobProperty); set => SetValue(JobProperty, value); }
    public string PeakDisplay => (string)GetValue(PeakDisplayProperty);
    public string PeakBitrateDisplay => (string)GetValue(PeakBitrateDisplayProperty);

    public DownloadRateGraph()
    {
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Sample(), Dispatcher);
        _timer.Stop();
        Loaded += (_, _) => { Sample(); UpdateSampling(); };
        Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateSampling();
    }

    private void UpdateSampling()
    {
        DependencyObject? owner = this;
        while (owner is not null && owner is not Page) owner = VisualTreeHelper.GetParent(owner);
        if (IsLoaded && (owner is Page page ? page.IsVisible : IsVisible)) _timer.Start();
        else _timer.Stop();
    }

    private void Sample()
    {
        if (!ReferenceEquals(_sampledJob, Job)) { _sampledJob = Job; _samples.Clear(); }
        var rate = Job is { IsActive: true } ? Job.BytesPerSecond : 0;
        Job?.RecordTransferRate(rate);
        _samples.Clear();
        if (Job is { } job) foreach (var sample in job.GetTransferRateHistory()) _samples.Enqueue(sample);
        var peak = Job?.SessionPeakBytesPerSecond ?? 0;
        SetValue(PeakDisplayPropertyKey, peak > 0 ? (peak / 1048576).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s" : "—");
        SetValue(PeakBitrateDisplayPropertyKey, DownloadJob.FormatBitrate(peak));
        if (IsVisible) InvalidateVisual();
    }

    private void ResetHistory()
    {
        _sampledJob = Job; _samples.Clear();
        SetValue(PeakDisplayPropertyKey, "—");
        SetValue(PeakBitrateDisplayPropertyKey, "—");
        if (IsLoaded) Sample();
        else InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(32, 184, 182, 180)), 1);
        for (var i = 1; i <= 3; i++) dc.DrawLine(grid, new Point(0, height * i / 3), new Point(width, height * i / 3));
        var values = _samples.ToArray();
        var maximum = values.Length == 0 ? 0 : values.Max();
        if (maximum <= 0) return;
        var accent = TryFindResource("DeckBlue") as Brush ?? Brushes.DodgerBlue;
        var line = new Pen(TryFindResource("DeckGreen") as Brush ?? Brushes.YellowGreen, 1.5);
        var step = width / 60;
        Point? previous = null;
        for (var i = 0; i < values.Length; i++)
        {
            var x = width - (values.Length - i) * step;
            var y = height - values[i] / maximum * height * .86;
            dc.PushOpacity(.72);
            dc.DrawRectangle(accent, null, new Rect(x, y, Math.Max(1, step - 2), height - y));
            dc.Pop();
            // Five measured samples form the green rolling average; no synthetic history.
            var average = values.Skip(Math.Max(0, i - 4)).Take(Math.Min(5, i + 1)).Average();
            var point = new Point(x + step / 2, height - average / maximum * height * .86);
            if (previous is { } start) dc.DrawLine(line, start, point);
            previous = point;
        }
    }
}
