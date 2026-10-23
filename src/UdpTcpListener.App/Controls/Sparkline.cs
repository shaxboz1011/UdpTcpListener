using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UdpTcpListener.App.Controls;

/// <summary>Minimal area chart for the live packets/second history.</summary>
public sealed class Sparkline : Control
{
    public static readonly StyledProperty<IReadOnlyList<double>?> ValuesProperty =
        AvaloniaProperty.Register<Sparkline, IReadOnlyList<double>?>(nameof(Values));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Stroke), Brushes.DodgerBlue);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Sparkline, IBrush?>(nameof(Fill));

    /// <summary>Number of points the chart is scaled for, so it scrolls in from the right.</summary>
    public static readonly StyledProperty<int> CapacityProperty =
        AvaloniaProperty.Register<Sparkline, int>(nameof(Capacity), 60);

    static Sparkline()
    {
        AffectsRender<Sparkline>(ValuesProperty, StrokeProperty, FillProperty, CapacityProperty);
    }

    public IReadOnlyList<double>? Values
    {
        get => GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public IBrush? Stroke
    {
        get => GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public int Capacity
    {
        get => GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var values = Values;
        var bounds = Bounds;
        if (values is null || values.Count < 2 || bounds.Width <= 0 || bounds.Height <= 0)
            return;

        const double pad = 2;
        var max = Math.Max(values.Max(), 1);
        var count = Math.Max(Capacity, values.Count);
        var step = (bounds.Width - pad * 2) / (count - 1);
        var startX = bounds.Width - pad - step * (values.Count - 1);
        var height = bounds.Height - pad * 2;

        Point At(int i) => new(startX + i * step, pad + height - values[i] / max * height);

        var line = new StreamGeometry();
        using (var g = line.Open())
        {
            g.BeginFigure(At(0), isFilled: false);
            for (var i = 1; i < values.Count; i++)
                g.LineTo(At(i));
            g.EndFigure(isClosed: false);
        }

        if (Fill is { } fill)
        {
            var area = new StreamGeometry();
            using (var g = area.Open())
            {
                g.BeginFigure(new Point(startX, bounds.Height), isFilled: true);
                for (var i = 0; i < values.Count; i++)
                    g.LineTo(At(i));
                g.LineTo(new Point(At(values.Count - 1).X, bounds.Height));
                g.EndFigure(isClosed: true);
            }
            context.DrawGeometry(fill, null, area);
        }

        context.DrawGeometry(null, new Pen(Stroke, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), line);
    }
}
