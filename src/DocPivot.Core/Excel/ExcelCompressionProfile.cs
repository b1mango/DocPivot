namespace DocPivot.Core.Excel;

public sealed record ExcelCompressionProfile
{
    public const double MinimumImageScale = 0.4d;
    public const int MaximumJpegQuality = 92;
    public const int MinimumJpegQuality = 58;

    private ExcelCompressionProfile(int strength, double imageScale, int jpegQuality)
    {
        Strength = strength;
        ImageScale = imageScale;
        JpegQuality = jpegQuality;
    }

    public int Strength { get; }

    public bool IsLossless => Strength == 0;

    public double ImageScale { get; }

    public int JpegQuality { get; }

    public static ExcelCompressionProfile FromStrength(int strength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(strength);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(strength, 100);

        if (strength == 0)
        {
            return new ExcelCompressionProfile(0, 1d, 100);
        }

        var progress = (strength - 1) / 99d;
        return new ExcelCompressionProfile(
            strength,
            1d - ((1d - MinimumImageScale) * progress),
            MaximumJpegQuality - (int)Math.Round(
                (MaximumJpegQuality - MinimumJpegQuality) * progress,
                MidpointRounding.AwayFromZero));
    }

    public static ExcelCompressionProfile FromLevel(ExcelImageCompressionLevel level) =>
        FromStrength(level switch
        {
            ExcelImageCompressionLevel.None => 0,
            ExcelImageCompressionLevel.HighQuality => 25,
            ExcelImageCompressionLevel.Balanced => 55,
            ExcelImageCompressionLevel.SmallFile => 85,
            _ => throw new ArgumentOutOfRangeException(nameof(level), level, "Unsupported Excel image compression level."),
        });
}
