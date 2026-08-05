namespace DocPivot.Core.Pdf;

public sealed record PdfCompressionProfile
{
    private PdfCompressionProfile(
        int strength,
        int? imageDpi,
        int? jpegQuality,
        PdfCompressionImpactLevel impactLevel)
    {
        Strength = strength;
        ImageDpi = imageDpi;
        JpegQuality = jpegQuality;
        ImpactLevel = impactLevel;
    }

    public int Strength { get; }

    public bool IsLossless => Strength == 0;

    public int? ImageDpi { get; }

    public int? JpegQuality { get; }

    public PdfCompressionImpactLevel ImpactLevel { get; }

    public static PdfCompressionProfile FromStrength(int strength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 100);

        if (strength == 0)
        {
            return new PdfCompressionProfile(0, null, null, PdfCompressionImpactLevel.None);
        }

        var impactLevel = strength switch
        {
            <= 33 => PdfCompressionImpactLevel.Low,
            <= 66 => PdfCompressionImpactLevel.Medium,
            _ => PdfCompressionImpactLevel.High,
        };
        return new PdfCompressionProfile(
            strength,
            InterpolateDescending(300, 150, strength),
            InterpolateDescending(92, 60, strength),
            impactLevel);
    }

    private static int InterpolateDescending(int maximum, int minimum, int strength) =>
        maximum - (int)Math.Round(
            (maximum - minimum) * (strength - 1) / 99d,
            MidpointRounding.AwayFromZero);
}
