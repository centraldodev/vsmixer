using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;

namespace VSMixer.Controls;

/// <summary>
/// Combined level meter + vertical fader: renders the live playback level and
/// doubles as the volume control (drag to set), so a track strip only needs one bar.
/// </summary>
public sealed class LevelMeter : Control
{
    public static readonly StyledProperty<double> LevelProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Level));

    public static readonly StyledProperty<double> VolumeProperty =
        AvaloniaProperty.Register<LevelMeter, double>(nameof(Volume), 1d, defaultBindingMode: BindingMode.TwoWay);

    public double Level
    {
        get => GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public double Volume
    {
        get => GetValue(VolumeProperty);
        set => SetValue(VolumeProperty, value);
    }

    static LevelMeter()
    {
        LevelProperty.Changed.AddClassHandler<LevelMeter>((meter, _) => meter.InvalidateVisual());
        VolumeProperty.Changed.AddClassHandler<LevelMeter>((meter, _) => meter.InvalidateVisual());
    }

    public LevelMeter()
    {
        Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);
            SetVolumeFromPointer(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (Equals(e.Pointer.Captured, this))
        {
            SetVolumeFromPointer(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Equals(e.Pointer.Captured, this))
        {
            e.Pointer.Capture(null);
        }
    }

    private void SetVolumeFromPointer(Point position)
    {
        if (Bounds.Height <= 0)
        {
            return;
        }

        Volume = Math.Clamp(1 - position.Y / Bounds.Height, 0, 1);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#050b12")), bounds, 7);

        var inner = bounds.Deflate(4);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#08131d")), inner, 5);

        var level = Math.Clamp(Level, 0, 1);
        var activeHeight = inner.Height * level;
        var active = new Rect(inner.X, inner.Bottom - activeHeight, inner.Width, activeHeight);

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

        context.FillRectangle(brush, active, 5);

        var segmentPen = new Pen(new SolidColorBrush(Color.Parse("#0f1d2b")), 1);
        for (var y = inner.Bottom; y > inner.Top; y -= 6)
        {
            context.DrawLine(segmentPen, new Point(inner.X, y), new Point(inner.Right, y));
        }

        var volume = Math.Clamp(Volume, 0, 1);
        var handleY = inner.Top + inner.Height * (1 - volume);
        var handleBounds = new Rect(1, Math.Clamp(handleY - 2, 1, bounds.Height - 5), bounds.Width - 2, 4);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#eef5ff")), handleBounds, 2);
        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#9db8d4")), 1), handleBounds, 2);

        context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#2f465e")), 1), bounds.Deflate(0.5), 7);
    }
}
