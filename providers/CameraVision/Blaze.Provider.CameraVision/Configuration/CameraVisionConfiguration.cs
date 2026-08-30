using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Blaze.Provider.CameraVision;

internal sealed record CameraDeviceProfile(
    CameraCaptureMode Mode,
    bool MirrorX,
    CameraRotation Rotation);

internal sealed class CameraVisionConfiguration
{
    public const int CurrentSchemaVersion = 3;

    [JsonConstructor]
    public CameraVisionConfiguration(
        int schemaVersion,
        CameraCaptureOptions capture,
        int maxHands,
        float minDetectionConfidence,
        float minTrackingConfidence,
        HandTrackingPoint trackingPoint,
        float smoothingFactor,
        float maximumMatchDistance,
        int lostFrameTolerance,
        IReadOnlyDictionary<string, CameraCalibration>? calibrations,
        IReadOnlyDictionary<int, CameraDeviceProfile>? deviceProfiles = null,
        bool flipX = false,
        bool flipY = false)
    {
        if (schemaVersion is < 1 or > CurrentSchemaVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                $"CameraVision configuration schema must be between 1 and {CurrentSchemaVersion}.");
        }

        Capture = capture ?? throw new ArgumentNullException(nameof(capture));
        Capture.Validate();
        if (maxHands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHands), "MaxHands must be positive.");
        }

        ValidateConfidence(minDetectionConfidence, nameof(minDetectionConfidence));
        ValidateConfidence(minTrackingConfidence, nameof(minTrackingConfidence));
        if (!Enum.IsDefined(trackingPoint))
        {
            throw new ArgumentOutOfRangeException(nameof(trackingPoint));
        }

        if (!float.IsFinite(smoothingFactor) || smoothingFactor is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(smoothingFactor),
                "SmoothingFactor must be finite and between zero and one.");
        }

        if (!float.IsFinite(maximumMatchDistance) || maximumMatchDistance <= 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumMatchDistance),
                "MaximumMatchDistance must be finite and positive.");
        }

        if (lostFrameTolerance <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(lostFrameTolerance),
                "LostFrameTolerance must be positive.");
        }

        var calibrationSnapshot = new Dictionary<string, CameraCalibration>(StringComparer.Ordinal);
        if (calibrations is not null)
        {
            foreach (var (surfaceId, calibration) in calibrations)
            {
                if (string.IsNullOrWhiteSpace(surfaceId))
                {
                    throw new ArgumentException(
                        "Calibration surface IDs cannot be empty.",
                        nameof(calibrations));
                }

                calibrationSnapshot.Add(
                    surfaceId,
                    calibration ?? throw new ArgumentException(
                        "Surface calibrations cannot be null.",
                        nameof(calibrations)));
            }
        }

        var profileSnapshot = new Dictionary<int, CameraDeviceProfile>();
        if (schemaVersion >= 2 && deviceProfiles is not null)
        {
            foreach (var (deviceIndex, profile) in deviceProfiles)
            {
                if (deviceIndex < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(deviceProfiles),
                        "Camera profile device indexes cannot be negative.");
                }

                if (profile is null || profile.Mode is null || !Enum.IsDefined(profile.Rotation))
                {
                    throw new ArgumentException(
                        "Camera device profiles are invalid.",
                        nameof(deviceProfiles));
                }

                profileSnapshot.Add(deviceIndex, profile);
            }
        }

        if (!profileSnapshot.ContainsKey(Capture.DeviceIndex))
        {
            profileSnapshot.Add(
                Capture.DeviceIndex,
                new CameraDeviceProfile(
                    new CameraCaptureMode(
                        Capture.Width,
                        Capture.Height,
                        Capture.FramesPerSecond),
                    Capture.MirrorX,
                    Capture.Rotation));
        }

        SchemaVersion = CurrentSchemaVersion;
        MaxHands = maxHands;
        MinDetectionConfidence = minDetectionConfidence;
        MinTrackingConfidence = minTrackingConfidence;
        TrackingPoint = trackingPoint;
        SmoothingFactor = smoothingFactor;
        MaximumMatchDistance = maximumMatchDistance;
        LostFrameTolerance = lostFrameTolerance;
        FlipX = flipX;
        FlipY = flipY;
        Calibrations = new ReadOnlyDictionary<string, CameraCalibration>(calibrationSnapshot);
        DeviceProfiles = new ReadOnlyDictionary<int, CameraDeviceProfile>(profileSnapshot);
    }

    public int SchemaVersion { get; }
    public CameraCaptureOptions Capture { get; }
    public int MaxHands { get; }
    public float MinDetectionConfidence { get; }
    public float MinTrackingConfidence { get; }
    public HandTrackingPoint TrackingPoint { get; }
    public float SmoothingFactor { get; }
    public float MaximumMatchDistance { get; }
    public int LostFrameTolerance { get; }
    public bool FlipX { get; }
    public bool FlipY { get; }
    public IReadOnlyDictionary<string, CameraCalibration> Calibrations { get; }
    public IReadOnlyDictionary<int, CameraDeviceProfile> DeviceProfiles { get; }

    public static CameraVisionConfiguration CreateDefault() => new(
        CurrentSchemaVersion,
        new CameraCaptureOptions(),
        maxHands: 8,
        minDetectionConfidence: 0.5f,
        minTrackingConfidence: 0.5f,
        HandTrackingPoint.IndexTip,
        smoothingFactor: 0.35f,
        maximumMatchDistance: 0.2f,
        lostFrameTolerance: 2,
        calibrations: null);

    private static void ValidateConfidence(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Confidence must be finite and between zero and one.");
        }
    }
}
