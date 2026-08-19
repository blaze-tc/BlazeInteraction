using Yuexin.Radar.Contracts;

namespace Yuexin.Radar.Processing;

public readonly record struct MappedScreenPoint(float PixelX, float PixelY, float NormalizedX, float NormalizedY);

public sealed class RadarOutputMapper
{
    private readonly RadarPixelRect _rect;
    private readonly int _screenWidth;
    private readonly int _screenHeight;

    public RadarOutputMapper(RadarPixelRect rect, int screenWidth, int screenHeight)
    {
        if (screenWidth <= 0 || screenHeight <= 0 || rect.Width <= 0 || rect.Height <= 0 ||
            rect.X < 0 || rect.Y < 0 || (long)rect.X + rect.Width > screenWidth ||
            (long)rect.Y + rect.Height > screenHeight)
        {
            throw new ArgumentOutOfRangeException(nameof(rect));
        }

        _rect = rect;
        _screenWidth = screenWidth;
        _screenHeight = screenHeight;
    }

    public MappedScreenPoint Map(float localNormalizedX, float localNormalizedY)
    {
        if (!float.IsFinite(localNormalizedX) || !float.IsFinite(localNormalizedY))
        {
            throw new ArgumentOutOfRangeException(nameof(localNormalizedX));
        }

        var pixelX = _rect.X + Math.Clamp(localNormalizedX, 0f, 1f) * _rect.Width;
        var pixelY = _rect.Y + Math.Clamp(localNormalizedY, 0f, 1f) * _rect.Height;
        return new MappedScreenPoint(pixelX, pixelY, pixelX / _screenWidth, pixelY / _screenHeight);
    }
}
