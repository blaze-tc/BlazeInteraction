namespace Yuexin.Radar.Processing;

public interface ICalibrationMapper
{
    bool IsValid { get; }

    bool TryMap(
        float physicalX,
        float physicalY,
        out float normalizedX,
        out float normalizedY);
}
