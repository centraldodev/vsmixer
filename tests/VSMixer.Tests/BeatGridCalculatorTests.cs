using VSMixer.Services;

namespace VSMixer.Tests;

public sealed class BeatGridCalculatorTests
{
    [Theory]
    [InlineData(0.25, "0.0.00")]
    [InlineData(0.50, "1.1.01")]
    [InlineData(1.00, "1.2.01")]
    [InlineData(2.00, "1.4.01")]
    [InlineData(2.50, "2.1.01")]
    public void FormatBarClock_UsesDetectedBeatOffset(double position, string expected)
    {
        Assert.Equal(expected, BeatGridCalculator.FormatBarClock(position, 120, 0.5, 4));
    }

    [Fact]
    public void GetMeasureStart_IncludesBeatGridOffset()
    {
        Assert.Equal(2.5, BeatGridCalculator.GetMeasureStart(2, 120, 0.5, 4), precision: 6);
    }
}
