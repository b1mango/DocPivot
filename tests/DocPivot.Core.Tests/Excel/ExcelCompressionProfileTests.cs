using DocPivot.Core.Excel;

namespace DocPivot.Core.Tests.Excel;

public sealed class ExcelCompressionProfileTests
{
    [Fact]
    public void FromStrength_ZeroDisablesImageProcessing()
    {
        var profile = ExcelCompressionProfile.FromStrength(0);

        Assert.True(profile.IsLossless);
        Assert.Equal(1d, profile.ImageScale);
    }

    [Fact]
    public void FromStrength_MapsEndpointsAndDecreasesContinuously()
    {
        var weakest = ExcelCompressionProfile.FromStrength(1);
        var strongest = ExcelCompressionProfile.FromStrength(100);

        Assert.False(weakest.IsLossless);
        Assert.Equal(1d, weakest.ImageScale);
        Assert.Equal(92, weakest.JpegQuality);
        Assert.Equal(0.4d, strongest.ImageScale, precision: 10);
        Assert.Equal(58, strongest.JpegQuality);
        var previous = weakest;
        for (var strength = 2; strength <= 100; strength++)
        {
            var current = ExcelCompressionProfile.FromStrength(strength);
            Assert.True(current.ImageScale < previous.ImageScale);
            Assert.True(current.JpegQuality <= previous.JpegQuality);
            previous = current;
        }
    }

    [Theory]
    [InlineData(ExcelImageCompressionLevel.None, 0)]
    [InlineData(ExcelImageCompressionLevel.HighQuality, 25)]
    [InlineData(ExcelImageCompressionLevel.Balanced, 55)]
    [InlineData(ExcelImageCompressionLevel.SmallFile, 85)]
    public void FromLevel_PreservesLegacyProfiles(ExcelImageCompressionLevel level, int expectedStrength)
    {
        Assert.Equal(ExcelCompressionProfile.FromStrength(expectedStrength), ExcelCompressionProfile.FromLevel(level));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void FromStrength_RejectsInvalidStrength(int strength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExcelCompressionProfile.FromStrength(strength));
    }
}
