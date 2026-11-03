using Avalonia.Controls;

namespace NudgeJudge.Views;

/// <summary>
/// Pages with a side editor: when the page is too narrow for the list and the editor side by side,
/// the root gets the <c>narrow</c> class and the page's styles turn the editor into a drawer over
/// the list with a scrim behind it. Clicking the scrim closes the editor.
/// </summary>
internal static class EditorDock
{
    /// <summary>Narrowest list worth showing beside the editor; below it the editor overlays instead.</summary>
    public const double ListMinWidth = 520;

    public static void Attach(Grid root, Control scrim, double editorWidth, Action close)
    {
        var threshold = ListMinWidth + editorWidth + 24;
        root.SizeChanged += (_, e) => root.Classes.Set("narrow", e.NewSize.Width < threshold);
        scrim.PointerPressed += (_, _) => close();
    }
}
