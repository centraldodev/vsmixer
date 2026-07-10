namespace VSMixer.Services;

public static class BeatGridCalculator
{
    public static int GetMeasure(double positionSeconds, double bpm, double offsetSeconds, int beatsPerMeasure)
    {
        var totalBeats = GetTotalBeats(positionSeconds, bpm, offsetSeconds);
        return (int)Math.Floor(totalBeats / Math.Max(1, beatsPerMeasure) + 1e-9) + 1;
    }

    public static double GetMeasureStart(int measure, double bpm, double offsetSeconds, int beatsPerMeasure)
    {
        var beatsBeforeMeasure = Math.Max(0, measure - 1) * Math.Max(1, beatsPerMeasure);
        return Math.Max(0, offsetSeconds) + beatsBeforeMeasure * 60d / Math.Max(1, bpm);
    }

    public static string FormatBarClock(double positionSeconds, double bpm, double offsetSeconds, int beatsPerMeasure)
    {
        if (positionSeconds < offsetSeconds)
        {
            return "0.0.00";
        }

        var safeBeatsPerMeasure = Math.Max(1, beatsPerMeasure);
        var totalBeats = GetTotalBeats(positionSeconds, bpm, offsetSeconds);
        var wholeBeat = Math.Max(0, (int)Math.Floor(totalBeats + 1e-9));
        var measure = wholeBeat / safeBeatsPerMeasure + 1;
        var beat = wholeBeat % safeBeatsPerMeasure + 1;
        var beatFraction = Math.Clamp(totalBeats - wholeBeat, 0, 0.999999);
        var tick = (int)Math.Floor(beatFraction * 100) + 1;
        return $"{measure}.{beat}.{tick:00}";
    }

    private static double GetTotalBeats(double positionSeconds, double bpm, double offsetSeconds)
    {
        var relativeSeconds = Math.Max(0, positionSeconds - Math.Max(0, offsetSeconds));
        return relativeSeconds * Math.Max(1, bpm) / 60d;
    }
}
