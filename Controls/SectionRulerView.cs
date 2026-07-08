using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using VSMixer.ViewModels;

namespace VSMixer.Controls;

public sealed class SectionRulerView : Control
{
    public static readonly StyledProperty<IEnumerable<SessionRegionViewModel>?> SectionsProperty =
        AvaloniaProperty.Register<SectionRulerView, IEnumerable<SessionRegionViewModel>?>(nameof(Sections));

    public static readonly StyledProperty<int> TotalMeasuresProperty =
        AvaloniaProperty.Register<SectionRulerView, int>(nameof(TotalMeasures), 176);

    public static readonly StyledProperty<ICommand?> EditSectionCommandProperty =
        AvaloniaProperty.Register<SectionRulerView, ICommand?>(nameof(EditSectionCommand));

    public static readonly StyledProperty<ICommand?> QueueSectionCommandProperty =
        AvaloniaProperty.Register<SectionRulerView, ICommand?>(nameof(QueueSectionCommand));

    public static readonly StyledProperty<ICommand?> OpenSectionMenuCommandProperty =
        AvaloniaProperty.Register<SectionRulerView, ICommand?>(nameof(OpenSectionMenuCommand));

    private readonly DispatcherTimer _blinkTimer;
    private bool _blinkOn = true;

    public SectionRulerView()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _blinkTimer.Tick += (_, _) =>
        {
            _blinkOn = !_blinkOn;
            InvalidateVisual();
        };
        _blinkTimer.Start();
    }

    public IEnumerable<SessionRegionViewModel>? Sections
    {
        get => GetValue(SectionsProperty);
        set => SetValue(SectionsProperty, value);
    }

    public int TotalMeasures
    {
        get => GetValue(TotalMeasuresProperty);
        set => SetValue(TotalMeasuresProperty, value);
    }

    public ICommand? EditSectionCommand
    {
        get => GetValue(EditSectionCommandProperty);
        set => SetValue(EditSectionCommandProperty, value);
    }

    public ICommand? QueueSectionCommand
    {
        get => GetValue(QueueSectionCommandProperty);
        set => SetValue(QueueSectionCommandProperty, value);
    }

    public ICommand? OpenSectionMenuCommand
    {
        get => GetValue(OpenSectionMenuCommandProperty);
        set => SetValue(OpenSectionMenuCommandProperty, value);
    }

    static SectionRulerView()
    {
        SectionsProperty.Changed.AddClassHandler<SectionRulerView>((view, args) =>
        {
            if (args.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= view.OnSectionsCollectionChanged;
            }

            view.UnsubscribeSectionChanges(args.OldValue as IEnumerable<SessionRegionViewModel>);

            if (args.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += view.OnSectionsCollectionChanged;
            }

            view.SubscribeSectionChanges(args.NewValue as IEnumerable<SessionRegionViewModel>);
            view.InvalidateVisual();
        });
        TotalMeasuresProperty.Changed.AddClassHandler<SectionRulerView>((view, _) => view.InvalidateVisual());
    }

    private void OnSectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var section in e.OldItems.OfType<SessionRegionViewModel>())
            {
                section.PropertyChanged -= OnSectionPropertyChanged;
            }
        }

        if (e.NewItems is not null)
        {
            foreach (var section in e.NewItems.OfType<SessionRegionViewModel>())
            {
                section.PropertyChanged += OnSectionPropertyChanged;
            }
        }

        InvalidateVisual();
    }

    private void OnSectionPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        InvalidateVisual();
    }

    private void SubscribeSectionChanges(IEnumerable<SessionRegionViewModel>? sections)
    {
        foreach (var section in sections ?? Enumerable.Empty<SessionRegionViewModel>())
        {
            section.PropertyChanged += OnSectionPropertyChanged;
        }
    }

    private void UnsubscribeSectionChanges(IEnumerable<SessionRegionViewModel>? sections)
    {
        foreach (var section in sections ?? Enumerable.Empty<SessionRegionViewModel>())
        {
            section.PropertyChanged -= OnSectionPropertyChanged;
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var section = GetSectionAt(e.GetPosition(this).X);
        if (section is null)
        {
            return;
        }

        var properties = e.GetCurrentPoint(this).Properties;
        var command = properties.IsRightButtonPressed
            ? OpenSectionMenuCommand
            : QueueSectionCommand;

        if (command is null || !command.CanExecute(section))
        {
            return;
        }

        command.Execute(section);
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = Bounds;
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0c1621")), bounds);

        var total = Math.Max(1, TotalMeasures);
        var measureWidth = bounds.Width / total;
        var tickPen = new Pen(new SolidColorBrush(Color.Parse("#2a3d4f")), 1);
        var strongTickPen = new Pen(new SolidColorBrush(Color.Parse("#47617a")), 1);
        var textBrush = new SolidColorBrush(Color.Parse("#f6fbff"));
        var typeface = new Typeface("Inter");

        for (var measure = 1; measure <= total; measure += 16)
        {
            var x = (measure - 1) * measureWidth;
            context.DrawLine(strongTickPen, new Point(x, 0), new Point(x, bounds.Height));
            context.DrawText(
                new FormattedText(measure.ToString(), System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, textBrush),
                new Point(x + 6, 9));
        }

        for (var measure = 1; measure <= total; measure += 4)
        {
            var x = (measure - 1) * measureWidth;
            context.DrawLine(tickPen, new Point(x, 0), new Point(x, bounds.Height));
        }

        foreach (var section in Sections ?? Enumerable.Empty<SessionRegionViewModel>())
        {
            var left = Math.Clamp((section.StartMeasure - 1) * measureWidth, 0, bounds.Width);
            var right = Math.Clamp(section.EndMeasure * measureWidth, left + 8, bounds.Width);
            var color = Color.Parse(section.Color);
            var alpha = section.IsQueued ? (_blinkOn ? (byte)255 : (byte)120) : (byte)220;
            var fill = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
            var rect = new Rect(left, 2, right - left, bounds.Height - 4);
            context.FillRectangle(fill, rect, 6);
            context.DrawRectangle(new Pen(new SolidColorBrush(Color.FromArgb(230, color.R, color.G, color.B)), 1), rect.Deflate(0.5), 6);

            if (section.IsQueued && _blinkOn)
            {
                context.DrawRectangle(new Pen(new SolidColorBrush(Color.Parse("#f6fbff")), 2), rect.Deflate(1), 5);
            }

            if (section.IsLooping)
            {
                context.DrawText(
                    new FormattedText("LOOP", System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 9, textBrush),
                    new Point(Math.Max(left + 8, right - 38), 8));
            }

            context.DrawText(
                new FormattedText(section.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, textBrush),
                new Point(left + 8, 7));
        }
    }

    private SessionRegionViewModel? GetSectionAt(double pointerX)
    {
        var bounds = Bounds;
        var total = Math.Max(1, TotalMeasures);
        var measureWidth = bounds.Width / total;

        return (Sections ?? Enumerable.Empty<SessionRegionViewModel>())
            .Reverse()
            .FirstOrDefault(section =>
            {
                var left = Math.Clamp((section.StartMeasure - 1) * measureWidth, 0, bounds.Width);
                var right = Math.Clamp(section.EndMeasure * measureWidth, left + 8, bounds.Width);
                return pointerX >= left && pointerX <= right;
            });
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (Sections is INotifyCollectionChanged collection)
        {
            collection.CollectionChanged -= OnSectionsCollectionChanged;
        }

        UnsubscribeSectionChanges(Sections);
        _blinkTimer.Stop();
    }
}
