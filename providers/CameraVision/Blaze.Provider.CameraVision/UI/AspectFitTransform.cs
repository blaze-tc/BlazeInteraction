using Blaze.Interaction.Contracts;

namespace Blaze.Provider.CameraVision;

internal readonly record struct AspectFitTransform(
    double Scale,
    double OffsetX,
    double OffsetY,
    double ContentWidth,
    double ContentHeight)
{
    internal static AspectFitTransform Create(
        double sourceWidth,
        double sourceHeight,
        double viewportWidth,
        double viewportHeight)
    {
        ValidateDimension(sourceWidth, nameof(sourceWidth));
        ValidateDimension(sourceHeight, nameof(sourceHeight));
        ValidateDimension(viewportWidth, nameof(viewportWidth));
        ValidateDimension(viewportHeight, nameof(viewportHeight));

        var scale = Math.Min(viewportWidth / sourceWidth, viewportHeight / sourceHeight);
        var contentWidth = sourceWidth * scale;
        var contentHeight = sourceHeight * scale;
        return new AspectFitTransform(
            scale,
            (viewportWidth - contentWidth) / 2d,
            (viewportHeight - contentHeight) / 2d,
            contentWidth,
            contentHeight);
    }

    internal Vector2Data SourceToViewport(Vector2Data source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new Vector2Data(
            checked((float)(OffsetX + source.X * Scale)),
            checked((float)(OffsetY + source.Y * Scale)));
    }

    internal bool TryViewportToSource(Vector2Data viewport, out Vector2Data source)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        var x = (double)viewport.X;
        var y = (double)viewport.Y;
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            x < OffsetX || x > OffsetX + ContentWidth ||
            y < OffsetY || y > OffsetY + ContentHeight)
        {
            source = null!;
            return false;
        }

        source = new Vector2Data(
            checked((float)((x - OffsetX) / Scale)),
            checked((float)((y - OffsetY) / Scale)));
        return true;
    }

    private static void ValidateDimension(double value, string parameterName)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }
}
