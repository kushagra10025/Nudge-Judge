using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using NudgeJudge.Core.Models;

namespace NudgeJudge.Controls;

/// <summary>
/// A distinct shape per match kind, so a rule list reads at a glance without the label:
/// domain = filled square, wildcard = open diamond, path prefix = filled arrowhead, regex = ring with dot.
/// </summary>
public sealed class MatchKindGlyph : Control
{
    public static readonly StyledProperty<MatchKind> KindProperty =
        AvaloniaProperty.Register<MatchKindGlyph, MatchKind>(nameof(Kind));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<MatchKindGlyph>();

    static MatchKindGlyph()
    {
        AffectsRender<MatchKindGlyph>(KindProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<MatchKindGlyph>(12);
        HeightProperty.OverrideDefaultValue<MatchKindGlyph>(12);
    }

    public MatchKind Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext ctx)
    {
        if (Foreground is null) return;
        var s = Math.Min(Bounds.Width, Bounds.Height);
        var c = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var pen = new Pen(Foreground, 1.5);
        var r = s / 2 - 1;

        switch (Kind)
        {
            case MatchKind.Domain:
                ctx.DrawRectangle(Foreground, null, new Rect(c.X - r * 0.8, c.Y - r * 0.8, r * 1.6, r * 1.6));
                break;
            case MatchKind.Wildcard:
                ctx.DrawGeometry(null, pen, Polygon(new(c.X, c.Y - r), new(c.X + r, c.Y), new(c.X, c.Y + r), new(c.X - r, c.Y)));
                break;
            case MatchKind.PathPrefix:
                ctx.DrawGeometry(Foreground, null, Polygon(new(c.X - r * 0.7, c.Y - r), new(c.X + r, c.Y), new(c.X - r * 0.7, c.Y + r)));
                break;
            case MatchKind.Regex:
                ctx.DrawEllipse(null, pen, c, r - 0.25, r - 0.25);
                ctx.DrawEllipse(Foreground, null, c, r * 0.32, r * 0.32);
                break;
        }
    }

    private static StreamGeometry Polygon(params Point[] points)
    {
        var g = new StreamGeometry();
        using var gc = g.Open();
        gc.BeginFigure(points[0], isFilled: true);
        foreach (var p in points.AsSpan(1)) gc.LineTo(p);
        gc.EndFigure(isClosed: true);
        return g;
    }
}
