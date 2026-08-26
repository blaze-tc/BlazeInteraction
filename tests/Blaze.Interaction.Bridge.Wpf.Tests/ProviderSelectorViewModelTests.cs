using System.Windows.Input;

namespace Blaze.Interaction.Bridge.Wpf.Tests;

public sealed class ProviderSelectorViewModelTests
{
    [Fact]
    public void Choices_UseProviderDescriptorsWithoutProviderTypeChecks()
    {
        var selection = Selection();

        var viewModel = Create(selection, savedProviderId: null, canCancel: false);

        Assert.Collection(
            viewModel.Choices,
            radar =>
            {
                Assert.Equal("blaze.radar.f10f20", radar.ProviderId);
                Assert.Equal("Radar sensing", radar.DisplayName);
                Assert.Equal("Radar", radar.Category);
            },
            camera =>
            {
                Assert.Equal("blaze.camera.vision", camera.ProviderId);
                Assert.Equal("Camera hand sensing", camera.DisplayName);
                Assert.Equal("Camera", camera.Category);
            });
    }

    [Fact]
    public void FirstRunWithoutSavedChoice_RequiresSelectionAndCannotCancel()
    {
        var viewModel = Create(Selection(), savedProviderId: null, canCancel: false);

        Assert.Null(viewModel.SelectedChoice);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
        Assert.False(viewModel.CanCancel);
        Assert.False(viewModel.CancelCommand.CanExecute(null));
    }

    [Fact]
    public void SavedAvailableProvider_IsPreselected()
    {
        var viewModel = Create(
            Selection(),
            savedProviderId: "blaze.camera.vision",
            canCancel: false);

        Assert.Equal("blaze.camera.vision", viewModel.SelectedChoice!.ProviderId);
        Assert.True(viewModel.ConfirmCommand.CanExecute(null));
        Assert.Null(viewModel.ErrorMessage);
    }

    [Fact]
    public void MissingSavedProvider_ReturnsToRequiredSelectionWithError()
    {
        var viewModel = Create(
            Selection(),
            savedProviderId: "blaze.missing",
            canCancel: false);

        Assert.Null(viewModel.SelectedChoice);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));
        Assert.Contains("blaze.missing", viewModel.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void ReturnFlow_CanCancelWithoutSelectingAProvider()
    {
        var selection = Selection();
        var viewModel = Create(
            selection,
            savedProviderId: "blaze.radar.f10f20",
            canCancel: true);
        var cancelled = 0;
        viewModel.Cancelled += () => cancelled++;

        viewModel.CancelCommand.Execute(null);

        Assert.Equal(1, cancelled);
        Assert.Equal(0, selection.SelectCalls);
    }

    [Fact]
    public async Task ConfirmCommand_ReentryCannotTriggerTwoSwitches()
    {
        var selection = Selection();
        selection.BlockSelection();
        var dispatcher = new RecordingDispatcher();
        var viewModel = Create(
            selection,
            savedProviderId: "blaze.camera.vision",
            canCancel: true,
            dispatcher);

        viewModel.ConfirmCommand.Execute(null);
        viewModel.ConfirmCommand.Execute(null);
        await selection.SelectionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, selection.SelectCalls);
        Assert.True(viewModel.IsBusy);
        Assert.False(viewModel.ConfirmCommand.CanExecute(null));

        selection.ReleaseSelection();
        await WaitUntilAsync(() => !viewModel.IsBusy);
        Assert.True(dispatcher.InvokeCalls >= 2);
    }

    [Fact]
    public async Task FailedSelection_ReportsErrorAndLeavesCurrentProviderActive()
    {
        var selection = Selection();
        selection.SelectionFailure = new InvalidOperationException("camera unavailable");
        var viewModel = Create(
            selection,
            savedProviderId: "blaze.camera.vision",
            canCancel: true);

        viewModel.ConfirmCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsBusy && selection.SelectCalls == 1);

        Assert.Equal("blaze.radar.f10f20", selection.ActiveProviderId);
        Assert.Contains("camera unavailable", viewModel.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SuccessfulConfirmation_RaisesConfirmedAfterSelectionCompletes()
    {
        var selection = Selection();
        var viewModel = Create(
            selection,
            savedProviderId: "blaze.camera.vision",
            canCancel: false);
        var confirmed = new TaskCompletionSource<ProviderChoiceViewModel>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Confirmed += choice => confirmed.TrySetResult(choice);

        viewModel.ConfirmCommand.Execute(null);
        var choice = await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("blaze.camera.vision", choice.ProviderId);
        Assert.Equal("blaze.camera.vision", selection.ActiveProviderId);
    }

    private static ProviderSelectorViewModel Create(
        RecordingSelection selection,
        string? savedProviderId,
        bool canCancel,
        IBridgeUiDispatcher? dispatcher = null) =>
        new(
            selection,
            dispatcher ?? new RecordingDispatcher(),
            savedProviderId,
            canCancel);

    private static RecordingSelection Selection() => new(
        [
            new BridgeAvailableProvider(
                "blaze.radar.f10f20",
                "radar-main",
                "Radar sensing",
                "Radar",
                true),
            new BridgeAvailableProvider(
                "blaze.camera.vision",
                "camera-main",
                "Camera hand sensing",
                "Camera",
                true)
        ],
        "blaze.radar.f10f20");

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class RecordingSelection(
        IReadOnlyList<BridgeAvailableProvider> availableProviders,
        string? activeProviderId) : IBridgeProviderSelection
    {
        private TaskCompletionSource? _release;

        public IReadOnlyList<BridgeAvailableProvider> AvailableProviders { get; } = availableProviders;
        public string? ActiveProviderId { get; private set; } = activeProviderId;
        public int SelectCalls { get; private set; }
        public Exception? SelectionFailure { get; set; }
        public TaskCompletionSource SelectionStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SelectProviderAsync(string providerId, CancellationToken cancellationToken)
        {
            SelectCalls++;
            SelectionStarted.TrySetResult();
            if (_release is not null)
            {
                await _release.Task.WaitAsync(cancellationToken);
            }

            if (SelectionFailure is not null)
            {
                throw SelectionFailure;
            }

            ActiveProviderId = providerId;
        }

        internal void BlockSelection() =>
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal void ReleaseSelection() => _release?.TrySetResult();
    }

    private sealed class RecordingDispatcher : IBridgeUiDispatcher
    {
        public int InvokeCalls { get; private set; }

        public Task InvokeAsync(Action action)
        {
            InvokeCalls++;
            action();
            return Task.CompletedTask;
        }
    }
}
