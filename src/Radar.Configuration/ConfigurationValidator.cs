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
        if (configuration.Screens.Count == 0) errors.Add("root.screens must contain at least one screen.");

        var screenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var associatedPrimaryCount = 0;
        var hasAssociatedScreen = false;
        foreach (var screen in configuration.Screens)
        {
            var screenPath = ScreenPath(screen);
            if (!IdPattern().IsMatch(screen.ScreenId ?? string.Empty)) errors.Add($"{screenPath}.screenId must match ^[a-z0-9_-]{{1,64}}$.");
            if (!screenIds.Add(screen.ScreenId ?? string.Empty)) errors.Add($"{screenPath}.screenId is a duplicate screenId.");
            hasAssociatedScreen |= screen.IsAssociated;
            if (screen.IsAssociated && screen.IsPrimary) associatedPrimaryCount++;
            ValidateScreen(screen, screenPath, errors, warnings);
        }

        if (hasAssociatedScreen && associatedPrimaryCount != 1) errors.Add("root.screens must have exactly one associated primary screen.");
        if (string.IsNullOrWhiteSpace(configuration.Ipc?.PipeName)) errors.Add("root.ipc.pipeName is required.");
        return new ConfigurationValidationResult(errors.Count == 0, errors, warnings);
    }

    private static void ValidateScreen(RadarScreenConfiguration screen, string path, ICollection<string> errors, ICollection<string> warnings)
    {
        var width = screen.EffectiveWidthPixels;
        var height = screen.EffectiveHeightPixels;
        if (width is < 1 or > MaximumPixels || height is < 1 or > MaximumPixels)
            errors.Add($"{path}.effectiveWidthPixels/effectiveHeightPixels must be between 1 and {MaximumPixels}.");

        if (screen.Fusion is null) errors.Add($"{path}.fusion is required.");
        else
        {
            if (screen.Fusion.OutputRateHz is < 1 or > 120) errors.Add($"{path}.fusion.outputRateHz must be between 1 and 120.");
            if (screen.Fusion.SensorDataMaxAgeMilliseconds is < 10 or > 5000) errors.Add($"{path}.fusion.sensorDataMaxAgeMilliseconds must be between 10 and 5000.");
            if (!IsFinitePositive(screen.Fusion.FusionDistancePixels)) errors.Add($"{path}.fusion.fusionDistancePixels must be finite and greater than 0.");
        }

        if (screen.Tracking is null) errors.Add($"{path}.tracking is required.");
        else
        {
            if (screen.Tracking.ConfirmFrames is < 1 or > 120 || screen.Tracking.LostFrames is < 1 or > 120)
                errors.Add($"{path}.tracking frame counts must be between 1 and 120.");
            if (!IsFinitePositive(screen.Tracking.MaximumAssociationDistancePixels)) errors.Add($"{path}.tracking.maximumAssociationDistancePixels must be finite and greater than 0.");
            if (!float.IsFinite(screen.Tracking.MaximumAssociationDistanceMeters) || screen.Tracking.MaximumAssociationDistanceMeters < 0f)
                errors.Add($"{path}.tracking.maximumAssociationDistanceMeters must be finite and non-negative.");
            if (!float.IsFinite(screen.Tracking.SmoothingAlpha) || screen.Tracking.SmoothingAlpha is <= 0f or > 1f)
                errors.Add($"{path}.tracking.smoothingAlpha must be greater than 0 and at most 1.");
        }

        if (screen.Interaction is null) errors.Add($"{path}.interaction is required.");
        else ValidateInteraction(screen.Interaction, path, errors);

        if (screen.Sensors is null || screen.Sensors.Count == 0)
        {
            errors.Add($"{path}.sensors must contain at least one radar sensor.");
            return;
        }

        var sensorIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sensor in screen.Sensors)
        {
            var sensorPath = $"{path}.sensors[{sensor.SensorId ?? "<missing>"}]";
            if (!IdPattern().IsMatch(sensor.SensorId ?? string.Empty)) errors.Add($"{sensorPath}.sensorId must match ^[a-z0-9_-]{{1,64}}$.");
            if (!sensorIds.Add(sensor.SensorId ?? string.Empty)) errors.Add($"{sensorPath}.sensorId is a duplicate sensorId.");
            ValidateSensor(sensor, sensorPath, width, height, errors, warnings);
        }
    }

    private static void ValidateInteraction(RadarInteractionConfiguration interaction, string path, ICollection<string> errors)
    {
        if (!Enum.IsDefined(interaction.Mode)) errors.Add($"{path}.interaction.mode is invalid.");
        if (interaction.DwellMilliseconds < 0 || interaction.MinimumPressMilliseconds < 0) errors.Add($"{path}.interaction durations must be non-negative.");
        if (!IsFiniteNonNegative(interaction.DwellRadiusNormalized) || !IsFiniteNonNegative(interaction.DragThresholdNormalized) || !IsFiniteNonNegative(interaction.MaximumClickMovementNormalized))
            errors.Add($"{path}.interaction normalized values must be finite and non-negative.");
    }

    private static void ValidateSensor(RadarSensorConfiguration sensor, string path, int width, int height, ICollection<string> errors, ICollection<string> warnings)
    {
        if (!Enum.IsDefined(sensor.SourceMode)) errors.Add($"{path}.sourceMode is invalid.");
        if (sensor.SourceMode == RadarSensorSourceMode.Real)
        {
            if (!IPAddress.TryParse(sensor.Device?.RadarIp, out _)) errors.Add($"{path}.device.radarIp must be a valid IP address.");
            if (sensor.Device?.Port is < 1 or > 65535) errors.Add($"{path}.device.port must be between 1 and 65535.");
        }
        if (!double.IsFinite(sensor.ReplaySpeed) || sensor.ReplaySpeed is < 0.1d or > 8d) errors.Add($"{path}.replaySpeed must be finite and between 0.1 and 8.0.");

        var rect = sensor.OutputRectPixels;
        if (rect.Width <= 0 || rect.Height <= 0 || rect.X < 0 || rect.Y < 0 || (long)rect.X + rect.Width > width || (long)rect.Y + rect.Height > height)
            errors.Add($"{path}.outputRectPixels must be within its screen.");

        ValidateSensorConfiguration(sensor, path, errors, warnings);
    }

    private static void ValidateSensorConfiguration(RadarSensorConfiguration sensor, string path, ICollection<string> errors, ICollection<string> warnings)
    {
        var device = sensor.Device ?? new RadarDeviceConfiguration();
        sensor.Device = device;
        if (!Enum.IsDefined(device.DeviceModel))
        {
            device.DeviceModel = RadarModel.F10;
            warnings.Add($"{path}.device.deviceModel was unknown and reset to F10.");
        }

        var range = sensor.Range ?? new RadarRangeConfiguration();
        sensor.Range = range;
        range.EdgeDeadZones ??= new RadarEdgeDeadZoneConfiguration();
        range.ActivePolygon ??= [];
        range.MaskedPolygons ??= [];
        var profile = RadarModelProfileFactory.Create(device.DeviceModel);
        if (!IsFinitePositive(range.MaximumDistanceMeters)) errors.Add($"{path}.range.maximumDistanceMeters must be finite and greater than 0.");
        else if (range.MaximumDistanceMeters > profile.MaximumDistanceMeters)
        {
            range.MaximumDistanceMeters = profile.MaximumDistanceMeters;
            warnings.Add($"{path}.range.maximumDistanceMeters was clamped to {profile.MaximumDistanceMeters} for {profile.Model}.");
        }
        if (!IsFinitePositive(range.VisualizationRangeMeters))
        {
            range.VisualizationRangeMeters = MathF.Min(4f, profile.MaximumDistanceMeters);
            warnings.Add($"{path}.range.visualizationRangeMeters was invalid and reset to the default view range.");
        }
        else if (range.VisualizationRangeMeters > profile.MaximumDistanceMeters)
        {
            range.VisualizationRangeMeters = profile.MaximumDistanceMeters;
            warnings.Add($"{path}.range.visualizationRangeMeters was clamped to {profile.MaximumDistanceMeters} for {profile.Model}.");
        }
        if (!IsFiniteNonNegative(range.MinimumDistanceMeters)) errors.Add($"{path}.range.minimumDistanceMeters must be finite and non-negative.");
        if (range.MaximumDistanceMeters <= range.MinimumDistanceMeters) errors.Add($"{path}.range.maximumDistanceMeters must be greater than minimumDistanceMeters.");
        if (!float.IsFinite(range.MinimumAngleDegrees) || !float.IsFinite(range.MaximumAngleDegrees) || range.MinimumAngleDegrees is < 0f or > 360f || range.MaximumAngleDegrees is < 0f or > 360f)
            errors.Add($"{path}.range angles must be finite and between 0 and 360 degrees.");
        if (!IsFiniteNonNegative(range.EdgeDeadZones.LeftMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.RightMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.TopMeters) || !IsFiniteNonNegative(range.EdgeDeadZones.BottomMeters))
            errors.Add($"{path}.range.edgeDeadZones values must be finite and non-negative.");
        ValidatePoints(range.ActivePolygon, $"{path}.range.activePolygon", errors);
        for (var index = 0; index < range.MaskedPolygons.Count; index++) ValidatePoints(range.MaskedPolygons[index], $"{path}.range.maskedPolygons[{index}]", errors);

        var transform = sensor.Transform ?? new RadarTransformConfiguration();
        sensor.Transform = transform;
        if (!float.IsFinite(transform.RotationDegrees) || !float.IsFinite(transform.OffsetXMeters) || !float.IsFinite(transform.OffsetYMeters))
            errors.Add($"{path}.transform values must be finite.");

        var clustering = sensor.Clustering ?? new RadarClusteringConfiguration();
        sensor.Clustering = clustering;
        if (clustering.MinimumClusterPointCount < 1) errors.Add($"{path}.clustering.minimumClusterPointCount must be at least 1.");
        if (!IsFinitePositive(clustering.BaseGapMeters) || !IsFiniteNonNegative(clustering.DistanceScale) || !IsFinitePositive(clustering.MaximumClusterWidthMeters))
            errors.Add($"{path}.clustering values must be finite and valid.");

        var calibration = sensor.Calibration ?? new RadarCalibrationConfiguration();
        sensor.Calibration = calibration;
        calibration.PhysicalCorners ??= [];
        calibration.HomographyMatrix ??= [];
        calibration.TransformSnapshot ??= new RadarTransformConfiguration();
        ValidatePoints(calibration.PhysicalCorners, $"{path}.calibration.physicalCorners", errors);
        if (calibration.HomographyMatrix.Any(value => !double.IsFinite(value))) errors.Add($"{path}.calibration.homographyMatrix values must be finite.");
        if (!double.IsFinite(calibration.MaximumCornerError) || calibration.MaximumCornerError < 0d) errors.Add($"{path}.calibration.maximumCornerError must be finite and non-negative.");
        if (!float.IsFinite(calibration.TransformSnapshot.RotationDegrees) || !float.IsFinite(calibration.TransformSnapshot.OffsetXMeters) || !float.IsFinite(calibration.TransformSnapshot.OffsetYMeters))
            errors.Add($"{path}.calibration.transformSnapshot values must be finite.");
        if (calibration.IsValid)
        {
            if (calibration.PhysicalCorners.Count != 4 || calibration.HomographyMatrix.Count != 9)
                errors.Add($"{path}.calibration requires four physical corners and a 3x3 homography matrix.");
            if (calibration.DeviceModel != device.DeviceModel) warnings.Add($"{path}.calibration was created for another radar model; review or recalibrate before use.");
        }
    }

    private static void ValidatePoints(IEnumerable<RadarPoint2> points, string path, ICollection<string> errors)
    {
        var index = 0;
        foreach (var point in points)
        {
            if (!float.IsFinite(point.X) || !float.IsFinite(point.Y)) errors.Add($"{path}[{index}] coordinates must be finite.");
            index++;
        }
    }

    private static string ScreenPath(RadarScreenConfiguration screen) => $"screens[{screen.ScreenId ?? "<missing>"}]";
    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
    private static bool IsFiniteNonNegative(float value) => float.IsFinite(value) && value >= 0f;
}
