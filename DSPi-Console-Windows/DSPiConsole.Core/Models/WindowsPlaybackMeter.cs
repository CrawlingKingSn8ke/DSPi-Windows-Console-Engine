namespace DSPiConsole.Core.Models;

public static class WindowsPlaybackMeter
{
    public static float Gain(float endpointDecibels, float sliderFraction, bool muted)
    {
        if (muted || sliderFraction <= 0)
            return 0;

        // The Windows slider is audio-tapered. Its fraction is not an amplitude gain.
        return float.IsFinite(endpointDecibels) ? MathF.Pow(10, endpointDecibels / 20) : 1;
    }
}
