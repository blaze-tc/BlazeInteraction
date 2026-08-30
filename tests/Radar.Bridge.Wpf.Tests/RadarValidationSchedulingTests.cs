using Yuexin.Radar.Bridge.Wpf.ViewModels;
using Yuexin.Radar.Configuration;
using Yuexin.Radar.Processing;

namespace Yuexin.Radar.Bridge.Wpf.Tests;

public sealed class RadarValidationSchedulingTests
{
    [Fact]
    public async Task DebouncedScheduler_OneHundredSchedules_PostsOnlyLatestValidation()
    {
        var context = new ManualRadarUiDispatcher();
        using var subject = new DebouncedValidationScheduler(TimeSpan.FromMilliseconds(30), context);
        var validationCount = 0;

        for (var index = 0; index < 100; index++)
        {
            subject.Schedule(() => validationCount++);
        }

        Assert.Equal(0, validationCount);
        await Task.Delay(100);
        Assert.Equal(1, context.PendingCount);

        context.Drain();

        Assert.Equal(1, validationCount);
    }

    [Fact]
    public async Task DebouncedScheduler_FlushRunsImmediatelyWithoutTimerDuplicate()
    {
        var context = new ManualRadarUiDispatcher();
        using var subject = new DebouncedValidationScheduler(TimeSpan.FromMilliseconds(30), context);
        var validationCount = 0;
        subject.Schedule(() => validationCount++);

        subject.Flush();

        Assert.Equal(1, validationCount);
        await Task.Delay(100);
        context.Drain();
        Assert.Equal(1, validationCount);
    }

    [Fact]
    public async Task DebouncedScheduler_DisposeDropsPendingValidation()
    {
        var context = new ManualRadarUiDispatcher();
        var subject = new DebouncedValidationScheduler(TimeSpan.FromMilliseconds(30), context);
        var validationCount = 0;
        subject.Schedule(() => validationCount++);

        subject.Dispose();
        await Task.Delay(100);
        context.Drain();

        Assert.Equal(0, validationCount);
    }

    [Fact]
    public void SensorEdits_NotifyEachFieldButDeferFullValidation()
    {
        var scheduler = new ManualValidationScheduler();
        var validationCount = 0;
        using var subject = new SensorItemViewModel(
            new RadarSensorConfiguration(),
            scheduler,
            _ =>
            {
                validationCount++;
                return true;
            });
        validationCount = 0;
        var radarIpNotifications = 0;
        subject.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(SensorItemViewModel.RadarIp))
            {
                radarIpNotifications++;
            }
        };

        for (var index = 0; index < 100; index++)
        {
            subject.RadarIp = $"10.0.0.{index}";
        }

        Assert.Equal(100, radarIpNotifications);
        Assert.Equal(0, validationCount);
        Assert.Equal(100, scheduler.ScheduleCount);

        scheduler.RunLatest();

        Assert.Equal(1, validationCount);
    }

    [Fact]
    public void SensorValidation_FlushRunsLatestValidationImmediately()
    {
        var scheduler = new ManualValidationScheduler();
        var validationCount = 0;
        using var subject = new SensorItemViewModel(
            new RadarSensorConfiguration(),
            scheduler,
            _ =>
            {
                validationCount++;
                return true;
            });
        validationCount = 0;
        subject.RadarIp = "10.1.2.3";

        subject.FlushValidation();

        Assert.Equal(1, scheduler.FlushCount);
        Assert.Equal(1, validationCount);
    }

    [Fact]
    public void ApplySnapshot_RaisesOneDisplayEventAndEachStatusNotificationOnce()
    {
        using var subject = new SensorItemViewModel(new RadarSensorConfiguration());
        var displayEvents = 0;
        var notifications = new Dictionary<string, int>();
        subject.SnapshotDisplayChanged += (_, _) => displayEvents++;
        subject.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is not null)
            {
                notifications[args.PropertyName] = notifications.GetValueOrDefault(args.PropertyName) + 1;
            }
        };
        var snapshot = RadarUiLoadFixture.CreateSnapshot(100, sequence: 7);

        subject.ApplySnapshot(snapshot);

        Assert.Equal(1, displayEvents);
        Assert.Equal(1, notifications[nameof(SensorItemViewModel.FrequencyText)]);
        Assert.Equal(1, notifications[nameof(SensorItemViewModel.CrcErrorCount)]);
        Assert.Equal(1, notifications[nameof(SensorItemViewModel.DroppedInputFrameCount)]);
        Assert.Equal(1, notifications[nameof(SensorItemViewModel.HasMatchedPhysicalTarget)]);
    }

    private sealed class ManualValidationScheduler : IValidationScheduler
    {
        private Action? _latest;

        public int ScheduleCount { get; private set; }
        public int FlushCount { get; private set; }

        public void Schedule(Action validation)
        {
            _latest = validation;
            ScheduleCount++;
        }

        public void Flush()
        {
            FlushCount++;
            RunLatest();
        }

        public void RunLatest()
        {
            var latest = _latest;
            _latest = null;
            latest?.Invoke();
        }

        public void Dispose() => _latest = null;
    }
}
