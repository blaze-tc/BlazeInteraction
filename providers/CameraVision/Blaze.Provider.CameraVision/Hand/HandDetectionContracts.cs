using System.Collections.ObjectModel;

namespace Blaze.Provider.CameraVision;

internal enum HandBackendStatus
{
    Uninitialized = 0,
    Initializing = 1,
    Ready = 2,
    Faulted = 3,
    Disposed = 4
}

internal sealed record HandDetectionOptions
{
    public HandDetectionOptions(
        string modelPath,
        int maxHands,
        float minDetectionConfidence,
        float minTrackingConfidence)
    {
        if (string.IsNullOrWhiteSpace(modelPath))
        {
            throw new ArgumentException("A hand landmarker model path is required.", nameof(modelPath));
        }

        if (maxHands <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxHands),
                "MaxHands must be positive.");
        }

        ValidateConfidence(minDetectionConfidence, nameof(minDetectionConfidence));
        ValidateConfidence(minTrackingConfidence, nameof(minTrackingConfidence));

        ModelPath = modelPath;
        MaxHands = maxHands;
        MinDetectionConfidence = minDetectionConfidence;
        MinTrackingConfidence = minTrackingConfidence;
    }

    public string ModelPath { get; }
    public int MaxHands { get; }
    public float MinDetectionConfidence { get; }
    public float MinTrackingConfidence { get; }

    private static void ValidateConfidence(float value, string parameterName)
    {
        if (!float.IsFinite(value) || value is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(parameterName,
                "Confidence must be finite and between zero and one.");
        }
    }
}

internal readonly record struct HandLandmark
{
    public HandLandmark(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
        {
            throw new ArgumentOutOfRangeException(nameof(x),
                "Hand landmark coordinates must be finite.");
        }

        X = x;
        Y = y;
        Z = z;
    }

    public float X { get; }
    public float Y { get; }
    public float Z { get; }
}

internal sealed class DetectedHand
{
    public const int LandmarkCount = 21;

    public DetectedHand(float confidence, IEnumerable<HandLandmark> landmarks)
    {
        if (!float.IsFinite(confidence) || confidence is < 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(confidence),
                "Hand confidence must be finite and between zero and one.");
        }

        ArgumentNullException.ThrowIfNull(landmarks);
        var snapshot = landmarks.ToArray();
        if (snapshot.Length != LandmarkCount)
        {
            throw new ArgumentException(
                $"A detected hand must contain exactly {LandmarkCount} landmarks.",
                nameof(landmarks));
        }

        Confidence = confidence;
        Landmarks = Array.AsReadOnly(snapshot);
    }

    public float Confidence { get; }
    public ReadOnlyCollection<HandLandmark> Landmarks { get; }
}

internal sealed class HandDetectionResult
{
    public static HandDetectionResult Empty { get; } = new(Array.Empty<DetectedHand>());

    public HandDetectionResult(IEnumerable<DetectedHand> hands)
    {
        ArgumentNullException.ThrowIfNull(hands);
        var snapshot = hands.ToArray();
        if (snapshot.Any(static hand => hand is null))
        {
            throw new ArgumentException("A hand result cannot contain null values.", nameof(hands));
        }

        Hands = Array.AsReadOnly(snapshot);
    }

    public ReadOnlyCollection<DetectedHand> Hands { get; }
}

internal readonly record struct RgbFrameView
{
    public RgbFrameView(nint data, int width, int height, int strideBytes)
    {
        if (data == 0)
        {
            throw new ArgumentException("The RGB frame pointer must not be null.", nameof(data));
        }

        if (width <= 0 || width > int.MaxValue / 3)
        {
            throw new ArgumentOutOfRangeException(nameof(width),
                "RGB frame width is invalid.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height),
                "RGB frame height must be positive.");
        }

        if (strideBytes < width * 3)
        {
            throw new ArgumentOutOfRangeException(nameof(strideBytes),
                "RGB frame stride must hold at least one packed RGB row.");
        }

        Data = data;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
    }

    public nint Data { get; }
    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
}

internal interface IHandDetectionBackend : IAsyncDisposable
{
    HandBackendStatus Status { get; }

    Task InitializeAsync(
        HandDetectionOptions options,
        CancellationToken cancellationToken);

    ValueTask<HandDetectionResult> DetectAsync(
        RgbFrameView frame,
        long timestampUnixMs,
        CancellationToken cancellationToken);
}
