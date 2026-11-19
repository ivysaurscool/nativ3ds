namespace PocketRecomp.Core.Display;

public enum ScreenLayout { TopOnly, Stacked, SideBySide }
public enum ScalingFilter { Nearest, Linear, Bicubic, Fsr }

public readonly record struct Viewport(int X, int Y, int Width, int Height);
public readonly record struct LayoutResult(Viewport Top, Viewport? Bottom, int OutputWidth, int OutputHeight);

public static class DisplayLayout
{
    public const int TopWidth = 400;
    public const int TopHeight = 240;
    public const int BottomWidth = 320;
    public const int BottomHeight = 240;

    public static LayoutResult Calculate(int outputWidth = 1920, int outputHeight = 1080,
        ScreenLayout layout = ScreenLayout.TopOnly, int gap = 16)
    {
        if (outputWidth <= 0 || outputHeight <= 0) throw new ArgumentOutOfRangeException(nameof(outputWidth));
        return layout switch
        {
            ScreenLayout.TopOnly => new(Fit(TopWidth, TopHeight, 0, 0, outputWidth, outputHeight), null, outputWidth, outputHeight),
            ScreenLayout.Stacked => CalculateStacked(outputWidth, outputHeight, Math.Max(0, gap)),
            ScreenLayout.SideBySide => CalculateSideBySide(outputWidth, outputHeight, Math.Max(0, gap)),
            _ => throw new ArgumentOutOfRangeException(nameof(layout))
        };
    }

    private static LayoutResult CalculateStacked(int width, int height, int gap)
    {
        var scale = Math.Min(width / (double)TopWidth, (height - gap) / 480.0);
        var topW = (int)Math.Round(TopWidth * scale);
        var topH = (int)Math.Round(TopHeight * scale);
        var bottomW = (int)Math.Round(BottomWidth * scale);
        var totalH = topH * 2 + gap;
        var top = new Viewport((width - topW) / 2, (height - totalH) / 2, topW, topH);
        var bottom = new Viewport((width - bottomW) / 2, top.Y + topH + gap, bottomW, topH);
        return new(top, bottom, width, height);
    }

    private static LayoutResult CalculateSideBySide(int width, int height, int gap)
    {
        var scale = Math.Min((width - gap) / 720.0, height / 240.0);
        var topW = (int)Math.Round(TopWidth * scale);
        var screenH = (int)Math.Round(TopHeight * scale);
        var bottomW = (int)Math.Round(BottomWidth * scale);
        var totalW = topW + gap + bottomW;
        var top = new Viewport((width - totalW) / 2, (height - screenH) / 2, topW, screenH);
        var bottom = new Viewport(top.X + topW + gap, top.Y, bottomW, screenH);
        return new(top, bottom, width, height);
    }

    private static Viewport Fit(int sourceW, int sourceH, int x, int y, int width, int height)
    {
        var scale = Math.Min(width / (double)sourceW, height / (double)sourceH);
        var w = (int)Math.Round(sourceW * scale);
        var h = (int)Math.Round(sourceH * scale);
        return new(x + (width - w) / 2, y + (height - h) / 2, w, h);
    }
}
