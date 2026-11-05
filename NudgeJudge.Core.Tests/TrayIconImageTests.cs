using NudgeJudge.Tray;

namespace NudgeJudge.Core.Tests;

/// <summary>The tray host reads the app icon without Avalonia; the Linux tray needs every size decodable.</summary>
public sealed class TrayIconImageTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(48)]
    public void Each_tray_size_of_the_embedded_icon_decodes(int size)
    {
        var (actual, png) = TrayIconImage.Best(size);
        Assert.Equal(size, actual);

        var (width, height, argb) = TrayIconImage.DecodeToArgb(png);

        Assert.Equal((size, size), (width, height));
        Assert.Equal(size * size * 4, argb.Length);
        // Not blank: some pixel is visible (alpha is the first byte of each ARGB pixel).
        Assert.Contains(argb.Where((_, i) => i % 4 == 0), alpha => alpha > 0);
    }
}
