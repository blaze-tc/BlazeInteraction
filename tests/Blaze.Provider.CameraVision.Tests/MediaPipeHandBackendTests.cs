namespace Blaze.Provider.CameraVision.Tests;

public sealed class MediaPipeHandBackendTests
{
    [Fact]
    public async Task InitializeAsync_RejectsAbiMismatchBeforeCreate()
    {
        var native = new NativeLibraryStub { AbiVersion = 2 };
        await using var backend = new MediaPipeHandBackend(native);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            backend.InitializeAsync(Options(), CancellationToken.None));

        Assert.Contains("ABI", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, native.CreateCalls);
        Assert.Equal(HandBackendStatus.Faulted, backend.Status);
    }

    [Fact]
    public async Task InitializeAsync_PropagatesCreateFailureWithoutLeakingHandle()
    {
        var native = new NativeLibraryStub
        {
            CreateStatus = 3,
            ErrorText = "model could not be loaded"
        };
        await using var backend = new MediaPipeHandBackend(native);

        var error = await Assert.ThrowsAsync<HandBackendException>(() =>
            backend.InitializeAsync(Options(), CancellationToken.None));

        Assert.Equal(3, error.StatusCode);
        Assert.Contains(native.ErrorText, error.Message, StringComparison.Ordinal);
        Assert.Equal(0, native.DestroyCalls);
        Assert.Equal(HandBackendStatus.Faulted, backend.Status);
    }

    [Fact]
    public async Task InitializeAsync_ReadsCreateErrorBeforeDestroyingReturnedFailureHandle()
    {
        var native = new NativeLibraryStub
        {
            CreateStatus = 3,
            FailureHandle = (nint)84,
            ErrorText = "partially created native state"
        };
        await using var backend = new MediaPipeHandBackend(native);

        var error = await Assert.ThrowsAsync<HandBackendException>(() =>
            backend.InitializeAsync(Options(), CancellationToken.None));

        Assert.Contains(native.ErrorText, error.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "create", "error", "destroy" }, native.Operations);
    }

    [Fact]
    public async Task DetectAsync_CopiesAllEightHandsAndTwentyOneLandmarks()
    {
        var native = NativeLibraryStub.WithHands(8);
        await using var backend = new MediaPipeHandBackend(native);
        await backend.InitializeAsync(Options(maxHands: 8), CancellationToken.None);

        var result = await backend.DetectAsync(ValidRgbFrame(), 1000, CancellationToken.None);

        Assert.Equal(8, result.Hands.Count);
        Assert.All(result.Hands, hand => Assert.Equal(21, hand.Landmarks.Count));
        Assert.Equal(7f, result.Hands[7].Landmarks[0].X);
        Assert.Equal(20f, result.Hands[0].Landmarks[20].Y);
        Assert.Equal(1, native.ProcessCalls);
        Assert.Equal(8, native.CopyCalls);
    }

    [Fact]
    public async Task DetectAsync_PropagatesNativeStatusAndBoundedErrorText()
    {
        var native = new NativeLibraryStub
        {
            ProcessStatus = 4,
            ErrorText = new string('x', 6000)
        };
        await using var backend = new MediaPipeHandBackend(native);
        await backend.InitializeAsync(Options(), CancellationToken.None);

        var error = await Assert.ThrowsAsync<HandBackendException>(async () =>
            await backend.DetectAsync(ValidRgbFrame(), 1000, CancellationToken.None));

        Assert.Equal(4, error.StatusCode);
        Assert.True(error.Message.Length < 5000);
        Assert.Equal(HandBackendStatus.Faulted, backend.Status);
    }

    [Fact]
    public async Task DetectAsync_CancellationBeforeNativeEntrySkipsProcessing()
    {
        var native = NativeLibraryStub.WithHands(1);
        await using var backend = new MediaPipeHandBackend(native);
        await backend.InitializeAsync(Options(), CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await backend.DetectAsync(ValidRgbFrame(), 1000, cancellation.Token));

        Assert.Equal(0, native.ProcessCalls);
        Assert.Equal(HandBackendStatus.Ready, backend.Status);
    }

    [Fact]
    public async Task DetectAsync_RequiresStrictlyIncreasingTimestamps()
    {
        var native = NativeLibraryStub.WithHands(1);
        await using var backend = new MediaPipeHandBackend(native);
        await backend.InitializeAsync(Options(), CancellationToken.None);
        await backend.DetectAsync(ValidRgbFrame(), 1000, CancellationToken.None);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await backend.DetectAsync(ValidRgbFrame(), 1000, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () =>
            await backend.DetectAsync(ValidRgbFrame(), 999, CancellationToken.None));

        Assert.Equal(1, native.ProcessCalls);
        Assert.Equal(new long[] { 1000 }, native.ProcessedTimestamps);
    }

    [Fact]
    public async Task DisposeAsync_DestroysInitializedHandleExactlyOnce()
    {
        var native = NativeLibraryStub.WithHands(1);
        var backend = new MediaPipeHandBackend(native);
        await backend.InitializeAsync(Options(), CancellationToken.None);

        await backend.DisposeAsync();
        await backend.DisposeAsync();

        Assert.Equal(1, native.DestroyCalls);
        Assert.Equal(1, native.DisposeCalls);
        Assert.Equal(HandBackendStatus.Disposed, backend.Status);
    }

    private static HandDetectionOptions Options(int maxHands = 8) =>
        new("model.task", maxHands, 0.5f, 0.5f);

    private static RgbFrameView ValidRgbFrame() => new((nint)1, 4, 3, 12);

    private sealed class NativeLibraryStub : INativeHandLibrary
    {
        private readonly List<NativeHandSnapshot> _hands = [];

        public uint AbiVersion { get; init; } = 1;
        public int CreateStatus { get; init; }
        public nint FailureHandle { get; init; }
        public int ProcessStatus { get; init; }
        public string ErrorText { get; init; } = "native failure";
        public int CreateCalls { get; private set; }
        public int ProcessCalls { get; private set; }
        public int CopyCalls { get; private set; }
        public int DestroyCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public List<long> ProcessedTimestamps { get; } = [];
        public List<string> Operations { get; } = [];

        public static NativeLibraryStub WithHands(int count)
        {
            var stub = new NativeLibraryStub();
            for (var handIndex = 0; handIndex < count; handIndex++)
            {
                var landmarks = Enumerable.Range(0, 21)
                    .Select(index => new NativeHandLandmark(handIndex, index, -index))
                    .ToArray();
                stub._hands.Add(new NativeHandSnapshot(0.8f, landmarks));
            }

            return stub;
        }

        public uint GetAbiVersion() => AbiVersion;

        public int Create(HandDetectionOptions options, out nint handle)
        {
            CreateCalls++;
            Operations.Add("create");
            handle = CreateStatus == 0 ? (nint)42 : FailureHandle;
            return CreateStatus;
        }

        public int ProcessFrame(nint handle, RgbFrameView frame, long timestampUnixMs)
        {
            ProcessCalls++;
            ProcessedTimestamps.Add(timestampUnixMs);
            return ProcessStatus;
        }

        public int GetHandCount(nint handle, out int handCount)
        {
            handCount = _hands.Count;
            return 0;
        }

        public int CopyHand(nint handle, int handIndex, out NativeHandSnapshot? hand)
        {
            CopyCalls++;
            hand = _hands[handIndex];
            return 0;
        }

        public int Destroy(nint handle)
        {
            DestroyCalls++;
            Operations.Add("destroy");
            return 0;
        }

        public string GetLastError(nint handle, int maxUtf8Bytes) =>
            RecordErrorRead(maxUtf8Bytes);

        private string RecordErrorRead(int maxUtf8Bytes)
        {
            Operations.Add("error");
            return ErrorText[..Math.Min(ErrorText.Length, maxUtf8Bytes)];
        }

        public void Dispose()
        {
            DisposeCalls++;
        }
    }
}
