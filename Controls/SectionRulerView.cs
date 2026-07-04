using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using VSMixer.ViewModels;

namespace VSMixer.Controls;

public sealed class SectionRulerView : Control
{
    public static readonly StyledProperty<IEnumerable<SessionRegionViewModel>?> SectionsProperty =
        AvaloniaProperty.Register<SectionRulerView, IEnumerable<SessionRegionViewModel>?>(nameof(Sections));

    public static readonly StyledProperty<int> TotalMeasuresProperty =
        AvaloniaProperty.Register<SectionRulerView, int>(nameof(TotalMeasures), 176);

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

    static SectionRulerView()
    {
        SectionsProperty.Changed.AddClassHandler<SectionRulerView>((view, args) =>
        {
            if (args.OldValue is INotifyCollectionChanged oldCollection)
            {
                oldCollection.CollectionChanged -= view.OnSectionsCollectionChanged;
            }

            if (args.NewValue is INotifyCollectionChanged newCollection)
            {
                newCollection.CollectionChanged += view.OnSectionsCollectionChanged;
            }

            view.InvalidateVisual();
        });
        TotalMeasuresProperty.Changed.AddClassHandler<SectionRulerView>((view, _) => view.InvalidateVisual());
    }

    private void OnSectionsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        InvalidateVisual();
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

        foreach (var section in Sections ?? Enumerable.Empty<SessionRegionViewModel>())
        {
            var left = Math.Clamp((section.StartMeasure - 1) * measureWidth, 0, bounds.Width);
            var right = Math.Clamp(section.EndMeasure * measureWidth, left + 8, bounds.Width);
            var color = Color.Parse(section.Color);
            var fill = new SolidColorBrush(Color.FromArgb(145, color.R, color.G, color.B));
            context.FillRectangle(fill, new Rect(left, 0, right - left, bounds.Height));
            context.DrawText(
                new FormattedText(section.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 11, textBrush),
                new Point(left + 8, 7));
        }

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
    }
}
