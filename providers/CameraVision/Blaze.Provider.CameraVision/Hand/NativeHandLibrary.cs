using System.Runtime.InteropServices;
using System.Text;

namespace Blaze.Provider.CameraVision;

internal readonly record struct NativeHandLandmark(float X, float Y, float Z);

internal sealed class NativeHandSnapshot
{
    public NativeHandSnapshot(float confidence, IEnumerable<NativeHandLandmark> landmarks)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        Confidence = confidence;
        Landmarks = Array.AsReadOnly(landmarks.ToArray());
    }

    public float Confidence { get; }
    public IReadOnlyList<NativeHandLandmark> Landmarks { get; }
}

internal interface INativeHandLibrary : IDisposable
{
    uint GetAbiVersion();
    int Create(HandDetectionOptions options, out nint handle);
    int ProcessFrame(nint handle, RgbFrameView frame, long timestampUnixMs);
    int GetHandCount(nint handle, out int handCount);
    int CopyHand(nint handle, int handIndex, out NativeHandSnapshot? hand);
    int Destroy(nint handle);
    string GetLastError(nint handle, int maxUtf8Bytes);
}

internal sealed class NativeHandLibrary : INativeHandLibrary
{
    internal const uint AbiVersion = 1;
    internal const string FileName = "Blaze.HandTracking.Native.dll";
    private const int LandmarkCount = 21;

    private nint _module;
    private readonly GetAbiVersionDelegate _getAbiVersion;
    private readonly CreateDelegate _create;
    private readonly ProcessFrameDelegate _processFrame;
    private readonly GetHandCountDelegate _getHandCount;
    private readonly CopyHandDelegate _copyHand;
    private readonly DestroyDelegate _destroy;
    private readonly GetLastErrorDelegate _getLastError;
    private int _disposed;

    public NativeHandLibrary(string? libraryPath = null)
    {
        var resolvedPath = libraryPath ?? ResolveProviderLocalPath();
        _module = NativeLibrary.Load(resolvedPath);
        try
        {
            _getAbiVersion = Resolve<GetAbiVersionDelegate>("blaze_hand_get_abi_version");
            _create = Resolve<CreateDelegate>("blaze_hand_create");
            _processFrame = Resolve<ProcessFrameDelegate>("blaze_hand_process_frame");
            _getHandCount = Resolve<GetHandCountDelegate>("blaze_hand_get_hand_count");
            _copyHand = Resolve<CopyHandDelegate>("blaze_hand_copy_hand");
            _destroy = Resolve<DestroyDelegate>("blaze_hand_destroy");
            _getLastError = Resolve<GetLastErrorDelegate>("blaze_hand_get_last_error");
        }
        catch
        {
            NativeLibrary.Free(_module);
            _module = 0;
            throw;
        }
    }

    public uint GetAbiVersion()
    {
        ThrowIfDisposed();
        return _getAbiVersion();
    }

    public int Create(HandDetectionOptions options, out nint handle)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(options);

        var modelPath = Marshal.StringToCoTaskMemUTF8(options.ModelPath);
        try
        {
            var nativeOptions = new NativeOptions
            {
                StructSize = checked((uint)Marshal.SizeOf<NativeOptions>()),
                MaxHands = options.MaxHands,
                MinDetectionConfidence = options.MinDetectionConfidence,
                MinTrackingConfidence = options.MinTrackingConfidence,
                ModelPathUtf8 = modelPath
            };
            return _create(in nativeOptions, out handle);
        }
        finally
        {
            Marshal.FreeCoTaskMem(modelPath);
        }
    }

    public int ProcessFrame(nint handle, RgbFrameView frame, long timestampUnixMs)
    {
        ThrowIfDisposed();
        return _processFrame(
            handle,
            frame.Data,
            frame.Width,
            frame.Height,
            frame.StrideBytes,
            timestampUnixMs);
    }

    public int GetHandCount(nint handle, out int handCount)
    {
        ThrowIfDisposed();
        return _getHandCount(handle, out handCount);
    }

    public unsafe int CopyHand(nint handle, int handIndex, out NativeHandSnapshot? hand)
    {
        ThrowIfDisposed();
        var status = _copyHand(
            handle,
            handIndex,
            out var nativeResult,
            checked((uint)sizeof(NativeResult)));
        if (status != 0)
        {
            hand = null;
            return status;
        }

        var landmarks = new NativeHandLandmark[LandmarkCount];
        for (var index = 0; index < LandmarkCount; index++)
        {
            var offset = index * 3;
            landmarks[index] = new NativeHandLandmark(
                nativeResult.Coordinates[offset],
                nativeResult.Coordinates[offset + 1],
                nativeResult.Coordinates[offset + 2]);
        }

        hand = new NativeHandSnapshot(nativeResult.Confidence, landmarks);
        return status;
    }

    public int Destroy(nint handle)
    {
        ThrowIfDisposed();
        return _destroy(handle);
    }

    public string GetLastError(nint handle, int maxUtf8Bytes)
    {
        ThrowIfDisposed();
        if (maxUtf8Bytes <= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxUtf8Bytes));
        }

        _getLastError(handle, 0, 0, out var requiredSize);
        var capacity = Math.Min(
            requiredSize == 0 ? 1u : requiredSize,
            checked((uint)maxUtf8Bytes));
        var buffer = Marshal.AllocHGlobal(checked((int)capacity));
        try
        {
            Marshal.WriteByte(buffer, 0);
            _getLastError(handle, buffer, capacity, out _);
            var bytes = new byte[checked((int)capacity)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var terminator = Array.IndexOf(bytes, (byte)0);
            var length = terminator >= 0 ? terminator : bytes.Length;
            return Encoding.UTF8.GetString(bytes, 0, length);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var module = Interlocked.Exchange(ref _module, 0);
        if (module != 0)
        {
            NativeLibrary.Free(module);
        }
    }

    private static string ResolveProviderLocalPath()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(NativeHandLibrary).Assembly.Location);
        if (string.IsNullOrEmpty(assemblyDirectory))
        {
            throw new InvalidOperationException("CameraVision assembly directory could not be resolved.");
        }

        var candidates = new[]
        {
            Path.Combine(assemblyDirectory, "runtimes", "win-x64", "native", FileName),
            Path.Combine(assemblyDirectory, FileName)
        };
        var path = candidates.FirstOrDefault(File.Exists);
        return path ?? throw new FileNotFoundException(
            $"The provider-local native hand library was not found. Checked: {string.Join(", ", candidates)}",
            FileName);
    }

    private T Resolve<T>(string exportName) where T : Delegate
    {
        var address = NativeLibrary.GetExport(_module, exportName);
        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeOptions
    {
        public uint StructSize { get; init; }
        public int MaxHands { get; init; }
        public float MinDetectionConfidence { get; init; }
        public float MinTrackingConfidence { get; init; }
        public nint ModelPathUtf8 { get; init; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeResult
    {
        public float Confidence;
        public fixed float Coordinates[LandmarkCount * 3];
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint GetAbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int CreateDelegate(in NativeOptions options, out nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int ProcessFrameDelegate(
        nint handle,
        nint rgbData,
        int width,
        int height,
        int strideBytes,
        long timestampUnixMs);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetHandCountDelegate(nint handle, out int handCount);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private unsafe delegate int CopyHandDelegate(
        nint handle,
        int handIndex,
        out NativeResult result,
        uint resultSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DestroyDelegate(nint handle);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetLastErrorDelegate(
        nint handle,
        nint utf8Buffer,
        uint bufferCapacity,
        out uint requiredSize);
}
