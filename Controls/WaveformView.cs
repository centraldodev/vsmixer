using System.Collections.Specialized;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VSMixer.Controls;

public sealed class WaveformView : Control
{
    public static readonly StyledProperty<IEnumerable<double>?> PeaksProperty =
        AvaloniaProperty.Register<WaveformView, IEnumerable<double>?>(nameof(Peaks));

    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<WaveformView, double>(nameof(Progress));

    public IEnumerable<double>? Peaks
    {
        get => GetValue(PeaksProperty);
        set => SetValue(PeaksProperty, value);
    }

    public double Progress
    {
        get => GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    static WaveformView()
    {
        PeaksProperty.Changed.AddClassHandler<WaveformView>((view, args) =>
        {
            if (args.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= view.OnPeaksCollectionChanged;
            }

            if (args.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += view.OnPeaksCollectionChanged;
            }

            view.InvalidateVisual();
        });
        ProgressProperty.Changed.AddClassHandler<WaveformView>((view, _) => view.InvalidateVisual());
    }

    private void OnPeaksCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#07101a")), bounds);

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#1a2a3a")), 1);
        for (var x = 0d; x < bounds.Width; x += 8)
        {
            context.DrawLine(gridPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        var center = bounds.Height / 2;
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#23384b")), 1), new Point(0, center), new Point(bounds.Width, center));

        var peakValues = Peaks?.ToArray() ?? Array.Empty<double>();
        if (peakValues.Length == 0 || bounds.Width <= 0)
        {
            return;
        }

        var barWidth = Math.Max(2, bounds.Width / peakValues.Length * 0.6);
        var step = bounds.Width / peakValues.Length;
        var waveformBrush = new SolidColorBrush(Color.Parse("#2d79ff"));
        var accentBrush = new SolidColorBrush(Color.Parse("#6eb0ff"));

        for (var i = 0; i < peakValues.Length; i++)
        {
            var normalized = Math.Clamp(peakValues[i], 0.04, 1);
            var height = normalized * bounds.Height * 0.8;
            var x = i * step + (step - barWidth) / 2;
            var y = center - height / 2;
            var brush = i % 23 == 0 ? accentBrush : waveformBrush;
            context.FillRectangle(brush, new Rect(x, y, barWidth, height), 1.5f);
        }

        var progressX = Math.Clamp(Progress, 0, 1) * bounds.Width;
        var playheadPen = new Pen(new SolidColorBrush(Color.Parse("#20f0d4")), 2);
        context.DrawLine(playheadPen, new Point(progressX, 0), new Point(progressX, bounds.Height));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#20f0d4")), new Rect(progressX - 4, 0, 8, 6), 2);
    }
}
