using System.Net;
using System.Text.RegularExpressions;
using Yuexin.Radar.Contracts;
using Yuexin.Radar.Device;

namespace Yuexin.Radar.Configuration;

public sealed record ConfigurationValidationResult(bool IsValid, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public static partial class ConfigurationValidator
{
    private const int MaximumPixels = 32768;

    [GeneratedRegex("^[a-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    public static ConfigurationValidationResult ValidateAndNormalize(RadarAppConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var errors = new List<string>();
        var warnings = new List<string>();

        configuration.Screens ??= [];
        if (configuration.Screens.Count == 0) errors.Add("screens must contain at least one screen.");

        var screenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var associatedPrimaryCount = 0;
        var hasAssociatedScreen = false;
        foreach (var screen in configuration.Screens)
        {
            if (!IdPattern().IsMatch(screen.ScreenId ?? string.Empty)) errors.Add("screenId must match ^[a-z0-9_-]{1,64}$.");
            if (!screenIds.Add(screen.ScreenId ?? string.Empty)) errors.Add($"duplicate screenId '{screen.ScreenId}'.");
            hasAssociatedScreen |= screen.IsAssociated;
            if (screen.IsAssociated && screen.IsPrimary) associatedPrimaryCount++;
            ValidateScreen(screen, errors, warnings);
        }

        if (hasAssociatedScreen && associatedPrimaryCount != 1) errors.Add("exactly one associated primary screen is required.");
        if (string.IsNullOrWhiteSpace(configuration.Ipc?.PipeName)) errors.Add("ipc.pipeName is required.");
        return new ConfigurationValidationResult(errors.Count == 0, errors, warnings);
    }

    private static void ValidateScreen(RadarScreenConfiguration screen, ICollection<string> errors, ICollection<string> warnings)
    {
        var width = screen.EffectiveWidthPixels;
        var height = screen.EffectiveHeightPixels;
        if (width is < 1 or > MaximumPixels || height is < 1 or > MaximumPixels)
            errors.Add($"screen '{screen.ScreenId}' effective width/height must be between 1 and {MaximumPixels}.");

        if (screen.Fusion is null) { errors.Add($"screen '{screen.ScreenId}' fusion is required."); return; }
        if (screen.Fusion.OutputRateHz is < 1 or > 120) errors.Add("fusion.outputRateHz must be between 1 and 120.");
        if (screen.Fusion.SensorDataMaxAgeMilliseconds is < 10 or > 5000) errors.Add("fusion.sensorDataMaxAgeMilliseconds must be between 10 and 5000.");
        if (!float.IsFinite(screen.Fusion.FusionDistancePixels) || screen.Fusion.FusionDistancePixels <= 0f)
            errors.Add("fusion.fusionDistancePixels must be finite and greater than 0.");

        if (screen.Tracking is null) { errors.Add($"screen '{screen.ScreenId}' tracking is required."); return; }
        if (screen.Tracking.ConfirmFrames is < 1 or > 120 || screen.Tracking.LostFrames is < 1 or > 120)
            errors.Add("tracking frame counts must be between 1 and 120.");
        if (!float.IsFinite(screen.Tracking.MaximumAssociationDistancePixels) || screen.Tracking.MaximumAssociationDistancePixels <= 0f)
            errors.Add("tracking.maximumAssociationDistancePixels must be finite and greater than 0.");
        if (!float.IsFinite(screen.Tracking.SmoothingAlpha) || screen.Tracking.SmoothingAlpha is <= 0f or > 1f)
            errors.Add("tracking.smoothingAlpha must be greater than 0 and at most 1.");

        if (screen.Interaction is null)
        {
            errors.Add($"screen '{screen.ScreenId}' interaction is required.");
        }
        else if (!float.IsFinite(screen.Interaction.DwellRadiusNormalized) ||
                 !float.IsFinite(screen.Interaction.DragThresholdNormalized) ||
                 !float.IsFinite(screen.Interaction.MaximumClickMovementNormalized))
        {
            errors.Add("interaction values must be finite.");
        }

        if (screen.Sensors is null || screen.Sensors.Count == 0)
        {
            errors.Add($"screen '{screen.ScreenId}' must contain at least one radar sensor.");
            return;
        }

        var sensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in screen.Sensors)
        {
            if (!IdPattern().IsMatch(sensor.SensorId ?? string.Empty)) errors.Add("sensorId must match ^[a-z0-9_-]{1,64}$.");
            if (!sensorIds.Add(sensor.SensorId ?? string.Empty)) errors.Add($"duplicate sensorId '{sensor.SensorId}' in screen '{screen.ScreenId}'.");
            ValidateSensor(screen, sensor, width, height, errors, warnings);
        }
    }

    private static void ValidateSensor(RadarScreenConfiguration screen, RadarSensorConfiguration sensor, int width, int height, ICollection<string> errors, ICollection<string> warnings)
    {
        if (!Enum.IsDefined(sensor.SourceMode)) errors.Add($"sensor '{sensor.SensorId}' sourceMode is invalid.");
        if (sensor.SourceMode == RadarSensorSourceMode.Real)
        {
            if (!IPAddress.TryParse(sensor.Device?.RadarIp, out _)) errors.Add("device.radarIp must be a valid IP address.");
            if (sensor.Device?.Port is < 1 or > 65535) errors.Add("device.port must be between 1 and 65535.");
        }
        if (!double.IsFinite(sensor.ReplaySpeed) || sensor.ReplaySpeed is < 0.1d or > 8d) errors.Add("replaySpeed must be finite and between 0.1 and 8.0.");

        var rect = sensor.OutputRectPixels;
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 ||
            (long)rect.X + rect.Width > width || (long)rect.Y + rect.Height > height)
            errors.Add($"sensor '{sensor.SensorId}' outputRectPixels must be within screen '{screen.ScreenId}'.");

        ValidateSensorConfiguration(sensor, errors, warnings);
    }

    private static void ValidateSensorConfiguration(RadarSensorConfiguration sensor, ICollection<string> errors, ICollection<string> warnings)
    {
        var device = sensor.Device ?? new RadarDeviceConfiguration();
        if (!Enum.IsDefined(device.DeviceModel))
        {
            device.DeviceModel = RadarModel.F10;
            sensor.Device = device;
            warnings.Add("deviceModel is unknown and was reset to F10.");
        }

        var range = sensor.Range ?? new RadarRangeConfiguration();
        sensor.Range = range;
        range.EdgeDeadZones ??= new RadarEdgeDeadZoneConfiguration();
        range.ActivePolygon ??= [];
        range.MaskedPolygons ??= [];
        var profile = RadarModelProfileFactory.Create(device.DeviceModel);
        if (!float.IsFinite(range.MaximumDistanceMeters) || range.MaximumDistanceMeters <= 0f) errors.Add("range.maximumDistanceMeters must be finite and greater than 0.");
        else if (range.MaximumDistanceMeters > profile.MaximumDistanceMeters)
        {
            range.MaximumDistanceMeters = profile.MaximumDistanceMeters;
            warnings.Add($"maximumDistanceMeters was clamped to {profile.MaximumDistanceMeters} for {profile.Model}.");
        }
        if (!float.IsFinite(range.VisualizationRangeMeters) || range.VisualizationRangeMeters <= 0f)
        {
            range.VisualizationRangeMeters = MathF.Min(4f, profile.MaximumDistanceMeters);
            warnings.Add("visualizationRangeMeters was invalid and reset to the default view range.");
        }
        else if (range.VisualizationRangeMeters > profile.MaximumDistanceMeters)
        {
            range.VisualizationRangeMeters = profile.MaximumDistanceMeters;
            warnings.Add($"visualizationRangeMeters was clamped to {profile.MaximumDistanceMeters} for {profile.Model}.");
        }
        if (!float.IsFinite(range.MinimumDistanceMeters) || range.MinimumDistanceMeters < 0f) errors.Add("range.minimumDistanceMeters cannot be negative or non-finite.");
        if (range.MaximumDistanceMeters <= range.MinimumDistanceMeters) errors.Add("range.maximumDistanceMeters must be greater than minimumDistanceMeters.");
        if (!float.IsFinite(range.MinimumAngleDegrees) || !float.IsFinite(range.MaximumAngleDegrees) || range.MinimumAngleDegrees is < 0f or > 360f || range.MaximumAngleDegrees is < 0f or > 360f)
            errors.Add("range angles must be finite and between 0 and 360 degrees.");
        if (!IsFiniteNonNegative(range.EdgeDeadZones.LeftMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.RightMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.TopMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.BottomMeters))
            errors.Add("range edge dead zones cannot be negative or non-finite.");

        var transform = sensor.Transform ?? new RadarTransformConfiguration();
        sensor.Transform = transform;
        if (!float.IsFinite(transform.RotationDegrees) || !float.IsFinite(transform.OffsetXMeters) || !float.IsFinite(transform.OffsetYMeters))
            errors.Add("transform values must be finite.");

        var clustering = sensor.Clustering ?? new RadarClusteringConfiguration();
        sensor.Clustering = clustering;
        if (clustering.MinimumClusterPointCount < 1) errors.Add("clustering.minimumClusterPointCount must be at least 1.");
        if (!IsFinitePositive(clustering.BaseGapMeters) || !IsFiniteNonNegative(clustering.DistanceScale) || !IsFinitePositive(clustering.MaximumClusterWidthMeters))
            errors.Add("clustering values must be finite and valid.");

        var calibration = sensor.Calibration ?? new RadarCalibrationConfiguration();
        sensor.Calibration = calibration;
        calibration.PhysicalCorners ??= [];
        calibration.HomographyMatrix ??= [];
        calibration.TransformSnapshot ??= new RadarTransformConfiguration();
        if (calibration.IsValid)
        {
            if (calibration.PhysicalCorners.Count != 4 || calibration.HomographyMatrix.Count != 9 || calibration.HomographyMatrix.Any(value => !double.IsFinite(value)))
                errors.Add("calibration requires four physical corners and a finite 3x3 homography matrix.");
            if (calibration.DeviceModel != device.DeviceModel) warnings.Add("The saved calibration was created for another radar model; review or recalibrate before use.");
        }
    }

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
    private static bool IsFiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;
}
