namespace Blaze.Provider.CameraVision;

internal enum HandTrackingPoint
{
    IndexTip = 0,
    PalmCenter = 1
}

internal readonly record struct CameraPoint(float X, float Y);
