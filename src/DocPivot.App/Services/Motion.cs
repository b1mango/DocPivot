using System.Windows;

namespace DocPivot.App.Services;

/// <summary>
/// One switch for interface motion: Windows' "Animation effects" setting (client-area animation).
/// When it is off, moves and fades land at once instead of playing.
/// </summary>
internal static class Motion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    /// <summary>The given duration while motion is on, zero when the user has turned animation off.</summary>
    public static Duration Pace(Duration duration) => Enabled ? duration : new Duration(TimeSpan.Zero);
}
