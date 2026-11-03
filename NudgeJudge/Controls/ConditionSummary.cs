using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using NudgeJudge.ViewModels;

namespace NudgeJudge.Controls;

/// <summary>
/// A rule's conditions on one trimmed line: match-kind glyphs and the app icon inline, patterns and
/// program names in mono, connecting words in the text face. A TextBlock, so it trims like the rest of the row.
/// </summary>
public sealed class ConditionSummary : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<SummaryPart>?> PartsProperty =
        AvaloniaProperty.Register<ConditionSummary, IReadOnlyList<SummaryPart>?>(nameof(Parts));

    public static readonly StyledProperty<IBrush?> GlyphBrushProperty =
        AvaloniaProperty.Register<ConditionSummary, IBrush?>(nameof(GlyphBrush));

    public static readonly StyledProperty<IBrush?> ValueBrushProperty =
        AvaloniaProperty.Register<ConditionSummary, IBrush?>(nameof(ValueBrush));

    static ConditionSummary()
    {
        PartsProperty.Changed.AddClassHandler<ConditionSummary>((s, _) => s.Rebuild());
        GlyphBrushProperty.Changed.AddClassHandler<ConditionSummary>((s, _) => s.Rebuild());
        ValueBrushProperty.Changed.AddClassHandler<ConditionSummary>((s, _) => s.Rebuild());
    }

    public ConditionSummary()
    {
        TextTrimming = TextTrimming.CharacterEllipsis;
        TextWrapping = TextWrapping.NoWrap;
    }

    protected override Type StyleKeyOverride => typeof(TextBlock);

    public IReadOnlyList<SummaryPart>? Parts { get => GetValue(PartsProperty); set => SetValue(PartsProperty, value); }
    public IBrush? GlyphBrush { get => GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
    public IBrush? ValueBrush { get => GetValue(ValueBrushProperty); set => SetValue(ValueBrushProperty, value); }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        var mono = this.TryFindResource("MonoFont", out var font) && font is FontFamily f ? f : FontFamily.Default;
        var appIcon = this.TryFindResource("Icon.App", out var g) ? g as Geometry : null;

        foreach (var part in Parts ?? [])
        {
            switch (part.Kind)
            {
                case SummaryPartKind.LinkGlyph:
                    inlines.Add(new InlineUIContainer(new MatchKindGlyph
                    {
                        Kind = part.Glyph,
                        Width = 10,
                        Height = 10,
                        Foreground = GlyphBrush,
                        Margin = new Thickness(0, 0, 6, -1),
                        VerticalAlignment = VerticalAlignment.Center,
                    }));
                    break;
                case SummaryPartKind.AppGlyph:
                    inlines.Add(new InlineUIContainer(new Icon
                    {
                        Data = appIcon,
                        Width = 12,
                        Height = 12,
                        StrokeThickness = 2,
                        Foreground = GlyphBrush,
                        Margin = new Thickness(0, 0, 5, -2),
                        VerticalAlignment = VerticalAlignment.Center,
                    }));
                    break;
                case SummaryPartKind.Value:
                    var run = new Run(part.Text) { FontFamily = mono };
                    if (ValueBrush is { } brush) run.Foreground = brush;
                    inlines.Add(run);
                    break;
                default:
                    inlines.Add(new Run(part.Text));
                    break;
            }
        }
        Inlines = inlines;
    }
}
