using LolHighlight.Desktop.Infrastructure;

namespace LolHighlight.Desktop.Tests;

public sealed class SliderValueScaleTests
{
    private static readonly double[] FrameRates = [30, 60, 120, 144, 165, 240];

    [Fact]
    public void DiscreteFrameRateSlider_NeverProducesIntermediateFpsValues()
    {
        for (var position = -1d; position <= FrameRates.Length; position += 0.01)
            Assert.Contains(SliderValueScale.StopAtPosition(position, FrameRates), FrameRates);
        Assert.Equal(144, SliderValueScale.StopAtPosition(3, FrameRates));
    }

    [Theory]
    [InlineData(60, 1, 120)]
    [InlineData(144, 1, 165)]
    [InlineData(165, -1, 144)]
    [InlineData(75, 1, 120)]
    [InlineData(75, -1, 60)]
    [InlineData(480, 1, 240)]
    [InlineData(480, -1, 240)]
    [InlineData(15, -1, 30)]
    [InlineData(240, 1, 240)]
    public void DiscreteKeyboardSteps_ChooseAdjacentValidStopsEvenForLegacyValues(double value, int direction, double expected)
        => Assert.Equal(expected, SliderValueScale.StepStop(value, FrameRates, direction));

    [Fact]
    public void StopDefinitions_IgnoreInvalidEntriesAndSortDistinctValues()
        => Assert.Equal(new double[] { 100, 200, 500, 1000 }, SliderValueScale.ParseStops("1000, 100, invalid,200,500,100,NaN,Infinity"));

    [Theory]
    [InlineData(75, 1, 480, 1, false)]
    [InlineData(768, 64, 8192, 64, true)]
    [InlineData(12.5, 0.1, 1000, 0.1, true)]
    [InlineData(250, 100, 60000, 50, true)]
    [InlineData(30, 0, 600, 1, false)]
    public void CommonAndCustomValues_RoundTripInTheirOwnUnits(double value, double minimum, double maximum, double step, bool logarithmic)
    {
        var position = SliderValueScale.ToPosition(value, minimum, maximum, logarithmic);
        Assert.InRange(position, 0, 1000);
        Assert.Equal(value, SliderValueScale.FromPosition(position, minimum, maximum, step, logarithmic), 6);
    }

    [Theory]
    [InlineData(0, 0.1)]
    [InlineData(1000, 1000)]
    [InlineData(500, 10)]
    public void LogarithmicBitrateScale_ProvidesSpaceForLowAndHighRates(double position, double expected)
        => Assert.Equal(expected, SliderValueScale.FromPosition(position, 0.1, 1000, 0.1, true), 6);

    [Fact]
    public void SliderBounds_KeepZeroDurationsAndRespectRaisedMaximumBitrateMinimum()
    {
        Assert.Equal(0, SliderValueScale.FromPosition(-100, 0, 600, 1, false));
        Assert.Equal(600, SliderValueScale.FromPosition(1100, 0, 600, 1, false));
        Assert.Equal(30.5, SliderValueScale.FromPosition(0, 30.5, 1000, 0.1, true));
    }
}
