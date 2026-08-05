using DocPivot.Core.Pdf;

namespace DocPivot.Core.Tests.Pdf;

public sealed class PdfCompressionProfileTests
{
    [Fact]
    public void FromStrength_ZeroCreatesLosslessProfile()
    {
        var profile = PdfCompressionProfile.FromStrength(0);

        Assert.True(profile.IsLossless);
        Assert.Null(profile.ImageDpi);
        Assert.Null(profile.JpegQuality);
        Assert.Equal(PdfCompressionImpactLevel.None, profile.ImpactLevel);
    }

    [Fact]
    public void FromStrength_MapsLossyEndpoints()
    {
        var weakest = PdfCompressionProfile.FromStrength(1);
        var strongest = PdfCompressionProfile.FromStrength(100);

        Assert.Equal(300, weakest.ImageDpi);
        Assert.Equal(92, weakest.JpegQuality);
        Assert.Equal(PdfCompressionImpactLevel.Low, weakest.ImpactLevel);
        Assert.Equal(150, strongest.ImageDpi);
        Assert.Equal(60, strongest.JpegQuality);
        Assert.Equal(PdfCompressionImpactLevel.High, strongest.ImpactLevel);
    }

    [Fact]
    public void FromStrength_IsMonotonicAcrossLossyRange()
    {
        var previous = PdfCompressionProfile.FromStrength(1);
        for (var strength = 2; strength <= 100; strength++)
        {
            var current = PdfCompressionProfile.FromStrength(strength);
            Assert.True(current.ImageDpi <= previous.ImageDpi);
            Assert.True(current.JpegQuality <= previous.JpegQuality);
            Assert.True(current.ImpactLevel >= previous.ImpactLevel);
            previous = current;
        }
    }

    [Theory]
    [InlineData(33, PdfCompressionImpactLevel.Low)]
    [InlineData(34, PdfCompressionImpactLevel.Medium)]
    [InlineData(66, PdfCompressionImpactLevel.Medium)]
    [InlineData(67, PdfCompressionImpactLevel.High)]
    public void FromStrength_AssignsStableImpactThresholds(
        int strength,
        PdfCompressionImpactLevel expected)
    {
        Assert.Equal(expected, PdfCompressionProfile.FromStrength(strength).ImpactLevel);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void FromStrength_RejectsOutOfRangeValue(int strength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfCompressionProfile.FromStrength(strength));
    }
}
