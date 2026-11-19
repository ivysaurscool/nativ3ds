namespace PocketRecomp.Core.Runtime;

/// <summary>
/// Separates the original game simulation rate from presentation rate. Rendering can run faster
/// immediately; simulation increases require a per-title patch so gameplay speed remains correct.
/// </summary>
public sealed record FrameTiming(double GuestFps = 60.0, double PresentationFps = 120.0)
{
    public TimeSpan GuestStep => TimeSpan.FromSeconds(1.0 / GuestFps);
    public TimeSpan PresentationStep => TimeSpan.FromSeconds(1.0 / PresentationFps);
    public double InterpolationFrames => PresentationFps / GuestFps;

    public FrameTiming Validate()
    {
        if (!double.IsFinite(GuestFps) || GuestFps <= 0 || GuestFps > 1000) throw new ArgumentOutOfRangeException(nameof(GuestFps));
        if (!double.IsFinite(PresentationFps) || PresentationFps <= 0 || PresentationFps > 1000) throw new ArgumentOutOfRangeException(nameof(PresentationFps));
        return this;
    }
}
