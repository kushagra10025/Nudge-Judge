using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using NudgeJudge.ViewModels;

namespace NudgeJudge.Controls;

/// <summary>
/// Signal-box route diagram. The link enters on the left and runs along the main line past
/// one junction per rule. At the matching junction the route diverts down to the browser's
/// platform; with no match it runs through to the "Ask" terminal. The set route lights amber,
/// drawn on with a short ease-out whenever the result changes.
/// </summary>
public sealed class RouteDiagram : Control
{
    public static readonly StyledProperty<IReadOnlyList<RouteStop>?> StopsProperty =
        AvaloniaProperty.Register<RouteDiagram, IReadOnlyList<RouteStop>?>(nameof(Stops));

    public static readonly StyledProperty<string?> DestinationProperty =
        AvaloniaProperty.Register<RouteDiagram, string?>(nameof(Destination));

    public static readonly StyledProperty<string> TerminalLabelProperty =
        AvaloniaProperty.Register<RouteDiagram, string>(nameof(TerminalLabel), "Ask");

    public static readonly StyledProperty<bool> IsIdleProperty =
        AvaloniaProperty.Register<RouteDiagram, bool>(nameof(IsIdle));

    private const double MainY = 26, BranchY = 58, LeftPad = 10, TerminalWidth = 128, Diverge = 22;
    private static readonly TimeSpan DrawOnDuration = TimeSpan.FromMilliseconds(320);

    private readonly DispatcherTimer _timer;
    private DateTime _animStart;
    private double _progress = 1;

    static RouteDiagram()
    {
        AffectsRender<RouteDiagram>(StopsProperty, DestinationProperty, TerminalLabelProperty, IsIdleProperty);
        HeightProperty.OverrideDefaultValue<RouteDiagram>(84);
        ClipToBoundsProperty.OverrideDefaultValue<RouteDiagram>(true);
    }

    public RouteDiagram()
    {
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public IReadOnlyList<RouteStop>? Stops { get => GetValue(StopsProperty); set => SetValue(StopsProperty, value); }
    public string? Destination { get => GetValue(DestinationProperty); set => SetValue(DestinationProperty, value); }
    public string TerminalLabel { get => GetValue(TerminalLabelProperty); set => SetValue(TerminalLabelProperty, value); }
    /// <summary>True when there's nothing to route (empty or invalid URL): draw the line unlit.</summary>
    public bool IsIdle { get => GetValue(IsIdleProperty); set => SetValue(IsIdleProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StopsProperty || change.Property == IsIdleProperty)
        {
            _progress = 0;
            _animStart = DateTime.UtcNow;
            _timer.Start();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Tick()
    {
        var t = (DateTime.UtcNow - _animStart).TotalMilliseconds / DrawOnDuration.TotalMilliseconds;
        _progress = t >= 1 ? 1 : 1 - Math.Pow(1 - t, 4); // ease-out quart
        if (t >= 1) _timer.Stop();
        InvalidateVisual();
    }

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        if (w < 120) return;

        var track = Brush("TrackBrush");
        var route = Brush("RouteBrush");
        var ink = Brush("InkBrush");
        var muted = Brush("InkFaintBrush");
        var stop = Brush("StopBrush");
        var ground = Brush("SurfaceBrush");

        var stops = Stops ?? [];
        var lineEnd = w - TerminalWidth;
        var firstX = LeftPad + 34;
        var span = Math.Max(0, lineEnd - firstX - Diverge - 40);
        var spacing = stops.Count > 1 ? Math.Min(64, span / (stops.Count - 1)) : 0;
        var labelEvery = spacing >= 18 ? 1 : (int)Math.Ceiling(18 / Math.Max(spacing, 1));
        double X(int i) => firstX + i * spacing;

        var matched = -1;
        for (var i = 0; i < stops.Count; i++)
            if (stops[i].State == RouteStopState.Matched) { matched = i; break; }

        var trackPen = new Pen(track, 3, lineCap: PenLineCap.Round);
        var routePen = new Pen(route, 3.5, lineCap: PenLineCap.Round);

        // Static layout: main line, terminal, and (when matched) the branch to the platform.
        ctx.DrawLine(trackPen, new Point(LeftPad, MainY), new Point(lineEnd, MainY));
        ctx.DrawRectangle(track, null, new Rect(lineEnd + 2, MainY - 7, 4, 14), 1, 1);
        Text(ctx, TerminalLabel, new Point(lineEnd + 12, MainY - 8), matched < 0 && !IsIdle ? ink : muted, 12, FontWeight.SemiBold);

        Point branchEnd = default;
        if (matched >= 0)
        {
            var bx = X(matched);
            branchEnd = new Point(lineEnd, BranchY);
            ctx.DrawLine(trackPen, new Point(bx, MainY), new Point(bx + Diverge, BranchY));
            ctx.DrawLine(trackPen, new Point(bx + Diverge, BranchY), branchEnd);
            ctx.DrawRectangle(track, null, new Rect(lineEnd + 2, BranchY - 7, 4, 14), 1, 1);
        }

        // Lit route, drawn on progressively.
        if (!IsIdle)
        {
            Point[] path = matched >= 0
                ? [new(LeftPad, MainY), new(X(matched), MainY), new(X(matched) + Diverge, BranchY), branchEnd]
                : [new(LeftPad, MainY), new(lineEnd, MainY)];
            DrawPartial(ctx, routePen, path, _progress);

            if (_progress >= 1)
            {
                var end = matched >= 0 ? BranchY : MainY;
                ctx.DrawRectangle(route, null, new Rect(lineEnd + 2, end - 7, 4, 14), 1, 1);
            }
        }

        if (matched >= 0)
            Text(ctx, Destination ?? "", new Point(lineEnd + 12, BranchY - 8), IsIdle ? muted : ink, 12, FontWeight.SemiBold);

        // Entry lamp.
        ctx.DrawEllipse(IsIdle ? track : route, null, new Point(LeftPad, MainY), 5, 5);

        // Junctions.
        for (var i = 0; i < stops.Count; i++)
        {
            var p = new Point(X(i), MainY);
            var s = stops[i].State;
            switch (s)
            {
                case RouteStopState.Matched:
                    ctx.DrawGeometry(route, new Pen(ground, 2), Diamond(p, 7));
                    break;
                case RouteStopState.Error:
                    ctx.DrawEllipse(ground, new Pen(stop, 2), p, 4.5, 4.5);
                    break;
                case RouteStopState.Inactive:
                    ctx.DrawEllipse(ground, new Pen(track, 1.5, new DashStyle([1.5, 1.5], 0)), p, 4.5, 4.5);
                    break;
                case RouteStopState.Passed:
                    ctx.DrawEllipse(ground, new Pen(IsIdle ? track : route, 2), p, 4, 4);
                    break;
                default:
                    ctx.DrawEllipse(ground, new Pen(track, 2), p, 4, 4);
                    break;
            }

            if (i % labelEvery == 0 || s == RouteStopState.Matched)
            {
                var brush = s == RouteStopState.Matched ? ink : s == RouteStopState.Error ? stop : muted;
                Text(ctx, stops[i].Label, new Point(p.X, 2), brush, 11,
                    s == RouteStopState.Matched ? FontWeight.Bold : FontWeight.Normal, center: true);
            }
        }
    }

    private static void DrawPartial(DrawingContext ctx, IPen pen, Point[] path, double progress)
    {
        double total = 0;
        for (var i = 1; i < path.Length; i++) total += Distance(path[i - 1], path[i]);
        var remaining = total * progress;

        var g = new StreamGeometry();
        using (var gc = g.Open())
        {
            gc.BeginFigure(path[0], isFilled: false);
            for (var i = 1; i < path.Length && remaining > 0; i++)
            {
                var len = Distance(path[i - 1], path[i]);
                if (len <= remaining)
                {
                    gc.LineTo(path[i]);
                    remaining -= len;
                }
                else
                {
                    var f = remaining / len;
                    gc.LineTo(path[i - 1] + (path[i] - path[i - 1]) * f);
                    remaining = 0;
                }
            }
            gc.EndFigure(isClosed: false);
        }
        ctx.DrawGeometry(null, pen, g);
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    private static StreamGeometry Diamond(Point c, double r)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(new Point(c.X, c.Y - r), true);
        gc.LineTo(new Point(c.X + r, c.Y));
        gc.LineTo(new Point(c.X, c.Y + r));
        gc.LineTo(new Point(c.X - r, c.Y));
        gc.EndFigure(true);
        return g;
    }

    private void Text(DrawingContext ctx, string text, Point origin, IBrush brush, double size, FontWeight weight, bool center = false)
    {
        if (string.IsNullOrEmpty(text)) return;
        var family = this.TryFindResource("UiFont", ActualThemeVariant, out var f) && f is FontFamily ff ? ff : FontFamily.Default;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, weight), size, brush);
        if (!center) ft.MaxTextWidth = Math.Max(10, Bounds.Width - origin.X - 2);
        ft.MaxLineCount = 1;
        ft.Trimming = TextTrimming.CharacterEllipsis;
        ctx.DrawText(ft, center ? new Point(origin.X - ft.Width / 2, origin.Y) : origin);
    }

    private IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var v) && v is IBrush b ? b : Brushes.Gray;
}
