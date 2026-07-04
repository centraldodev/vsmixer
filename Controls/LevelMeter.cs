using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace VSMixer.Controls;

public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    static LevelMeter()
    {
        LevelProperty.Changed.AddClassHandler<LevelMeter>((meter, _) => meter.InvalidateVisual());
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#050a10")), bounds, 2);

        var level = Math.Clamp(Level, 0, 1);
        var activeHeight = bounds.Height * level;
        var active = new Rect(0, bounds.Height - activeHeight, bounds.Width, activeHeight);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#21c35e"), 0),
                new GradientStop(Color.Parse("#d6b824"), 0.72),
                new GradientStop(Color.Parse("#e94949"), 1)
            }
        };

        context.FillRectangle(brush, active, 2);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#243445")), 1), bounds, 2);
    }
}
