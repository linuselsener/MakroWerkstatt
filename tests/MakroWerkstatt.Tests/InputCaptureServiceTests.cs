using MakroWerkstatt;
using System.Windows.Threading;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class InputCaptureServiceTests
{
    [Fact]
    public async Task Hotkeys_toggle_recording_and_playback_only_in_valid_states()
    {
        using var context = new TestContext();
        using var input = new FakeInputCaptureService();
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService(), input);
        input.Start();

        input.EmitHotkey(GlobalHotkeyAction.ToggleRecording);
        Assert.Equal(RecordingState.Recording, viewModel.RecordingState);
        input.EmitHotkey(GlobalHotkeyAction.TogglePlayback);
        Assert.False(viewModel.IsPlaying);
        Assert.Equal(RecordingState.Recording, viewModel.RecordingState);
        input.EmitHotkey(GlobalHotkeyAction.ToggleRecording);
        Assert.Equal(RecordingState.Idle, viewModel.RecordingState);

        viewModel.CurrentMacro = new MacroDocument { Name = "Synthetic playback" };
        viewModel.Actions.Add(new MacroAction { Type = MacroActionType.MouseMoveRelative, X = 1 });
        input.EmitHotkey(GlobalHotkeyAction.TogglePlayback);
        Assert.True(viewModel.IsPlaying);
        input.EmitHotkey(GlobalHotkeyAction.ToggleRecording);
        Assert.False(viewModel.IsRecording);
        input.EmitHotkey(GlobalHotkeyAction.TogglePlayback);
        await WaitForPlaybackToFinish(viewModel);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public void Captured_actions_are_accepted_only_while_recording()
    {
        using var context = new TestContext();
        using var input = new FakeInputCaptureService();
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService(), input);
        input.Start();
        var idleAction = new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 65 };

        input.EmitAction(idleAction);
        Assert.Empty(viewModel.Actions);

        viewModel.StartRecordingCommand.Execute(null);
        input.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 66 });
        Assert.Single(viewModel.Actions);

        viewModel.ToggleRecordingPauseCommand.Execute(null);
        input.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 67 });
        Assert.Single(viewModel.Actions);

        viewModel.ToggleRecordingPauseCommand.Execute(null);
        input.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 68 });
        Assert.Equal(2, viewModel.Actions.Count);
    }

    [Fact]
    public async Task Emergency_stop_cancels_playback_and_disables_escape_handling()
    {
        using var context = new TestContext();
        using var input = new FakeInputCaptureService();
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService(), input);
        input.Start();
        viewModel.CurrentMacro = new MacroDocument { Name = "Synthetic playback" };
        viewModel.Actions.Add(new MacroAction { Type = MacroActionType.MouseMoveRelative, X = 1 });

        input.EmitHotkey(GlobalHotkeyAction.TogglePlayback);
        Assert.True(viewModel.IsPlaying);
        Assert.True(input.EmergencyStopEnabled);
        input.EmitEmergencyStop();

        await WaitForPlaybackToFinish(viewModel);
        Assert.False(input.EmergencyStopEnabled);
        Assert.False(viewModel.IsPlaying);
    }

    [Fact]
    public void Input_events_queued_before_stop_and_after_dispose_do_not_change_view_model_state()
    {
        using var context = new TestContext();
        var dispatcher = new QueuedUiDispatcher { HasAccess = true };
        var hook = new FakeGlobalInputHook();
        using var service = new GlobalInputCaptureService(dispatcher, _ => hook);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService(), service);
        service.Start();
        viewModel.StartRecordingCommand.Execute(null);

        dispatcher.HasAccess = false;
        hook.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 70 });
        Assert.Equal(1, dispatcher.PendingCount);
        service.Stop();
        hook.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 69 });
        dispatcher.Drain();
        Assert.Empty(viewModel.Actions);

        Assert.Equal(1, hook.DisposeCount);

        using var secondContext = new TestContext();
        var secondHook = new FakeGlobalInputHook();
        using var secondService = new GlobalInputCaptureService(new ImmediateDispatcher(), _ => secondHook);
        using var secondViewModel = new MainViewModel(secondContext.Storage, new FakeDialogService(), secondService);
        secondService.Start();
        secondViewModel.StartRecordingCommand.Execute(null);
        secondViewModel.Dispose();
        secondHook.EmitAction(new MacroAction { Type = MacroActionType.KeyDown, KeyCode = 71 });
        Assert.Empty(secondViewModel.Actions);
    }

    [Fact]
    public void Hook_events_are_marshaled_once_with_the_original_dispatch_priorities()
    {
        var dispatcher = new ImmediateDispatcher();
        var hook = new FakeGlobalInputHook();
        using var service = new GlobalInputCaptureService(dispatcher, _ => hook);
        var captured = 0;
        var emergencies = 0;
        var hotkeys = 0;
        InputCaptureStatus? status = null;
        service.ActionCaptured += _ => captured++;
        service.EmergencyStopPressed += () => emergencies++;
        service.HotkeyPressed += (_, _) => hotkeys++;
        service.StatusChanged += value => status = value;
        service.Start();
        Assert.Equal(new InputCaptureStatus(true, true), status);

        dispatcher.HasAccess = false;
        hook.EmitAction(new MacroAction { Type = MacroActionType.MouseMove, X = 3, Y = 4 });
        dispatcher.HasAccess = true;
        hook.EmitEmergencyStop();
        hook.EmitHotkey(GlobalHotkeyAction.ToggleRecording, 0x11);

        Assert.Equal(1, captured);
        Assert.Equal(1, emergencies);
        Assert.Equal(1, hotkeys);
        Assert.Equal(new[] { DispatcherPriority.Input, DispatcherPriority.Send, DispatcherPriority.Send },
            dispatcher.PostedPriorities);
    }

    [Fact]
    public void Double_dispose_is_safe_and_unhooks_once()
    {
        var dispatcher = new ImmediateDispatcher();
        var hook = new FakeGlobalInputHook();
        var service = new GlobalInputCaptureService(dispatcher, _ => hook);
        service.Start();

        service.Dispose();
        service.Dispose();
        service.Stop();

        Assert.Equal(1, hook.DisposeCount);
    }

    [Fact]
    public void Startup_failure_disposes_the_partially_started_hook_once()
    {
        var dispatcher = new ImmediateDispatcher();
        var hook = new FakeGlobalInputHook { ThrowOnConfigure = true };
        var service = new GlobalInputCaptureService(dispatcher, _ => hook);

        Assert.Throws<InvalidOperationException>(service.Start);
        service.Stop();
        service.Dispose();

        Assert.Equal(1, hook.DisposeCount);
    }

    private static async Task WaitForPlaybackToFinish(MainViewModel viewModel)
    {
        for (var attempt = 0; attempt < 100 && viewModel.IsPlaying; attempt++)
            await Task.Delay(10);
        Assert.False(viewModel.IsPlaying);
    }
}
