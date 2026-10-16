using MakroWerkstatt;
using Xunit;

namespace MakroWerkstatt.Tests;

[Collection("SerialTests")]
public sealed class PlaybackPlanTests
{
    [Fact]
    public void Original_tempo_uses_recorded_action_times()
    {
        var macro = MacroWithTimes(0, 125, 400);

        var plan = PlaybackPlan.Create(macro, stretchEnabled: false, targetDurationMs: 9000,
            randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 80);

        Assert.Equal(1, plan.Scale);
        Assert.Equal(400, plan.BaseDurationMs);
        Assert.Equal(125, plan.DueTimeMs(1, plan.Actions[1]));
        Assert.Equal(400, plan.DueTimeMs(2, plan.Actions[2]));
    }

    [Fact]
    public void Stretching_scales_recorded_timing_to_target_duration()
    {
        var macro = MacroWithTimes(0, 500, 2000);

        var plan = PlaybackPlan.Create(macro, stretchEnabled: true, targetDurationMs: 8000,
            randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 80);

        Assert.Equal(4, plan.Scale);
        Assert.Equal(8000, plan.BaseDurationMs);
        Assert.Equal(2000, plan.DueTimeMs(1, plan.Actions[1]));
        Assert.Equal(8000, plan.DueTimeMs(2, plan.Actions[2]));
    }

    [Fact]
    public void Stretching_evenly_distributes_actions_without_measurable_gaps()
    {
        var macro = MacroWithTimes(0, 0, 0, 0);

        var plan = PlaybackPlan.Create(macro, stretchEnabled: true, targetDurationMs: 600,
            randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 80);

        Assert.True(plan.EvenlyDistribute);
        Assert.Equal(0, plan.DueTimeMs(0, plan.Actions[0]));
        Assert.Equal(200, plan.DueTimeMs(1, plan.Actions[1]));
        Assert.Equal(400, plan.DueTimeMs(2, plan.Actions[2]));
        Assert.Equal(600, plan.DueTimeMs(3, plan.Actions[3]));
    }

    [Fact]
    public void Repeat_count_boundaries_are_supported_and_invalid_values_disable_play_command()
    {
        using var context = new TestContext();
        var macro = new MacroDocument { Actions = [new MacroAction { AtMs = 0, Type = MacroActionType.KeyDown, KeyCode = 65 }] };
        context.Storage.Save(macro);
        using var viewModel = new MainViewModel(context.Storage, new FakeDialogService()) { InputHookAvailable = true };
        viewModel.LoadLibrary(showWarnings: false);
        viewModel.SelectedLibraryItem = Assert.Single(viewModel.Library);

        foreach (var count in new[] { 1, 100 })
        {
            viewModel.RepeatCount = count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            Assert.True(viewModel.PlayCommand.CanExecute(null));
            var plan = PlaybackPlan.Create(macro, stretchEnabled: false, targetDurationMs: 0,
                randomizerEnabled: false, randomMinMs: 1, randomMaxMs: 80, repeatCount: count);
            Assert.Equal(count, plan.RepeatCount);
        }

        foreach (var count in new[] { "0", "101", "not-a-number" })
        {
            viewModel.RepeatCount = count;
            Assert.False(viewModel.PlayCommand.CanExecute(null));
        }
    }

    private static MacroDocument MacroWithTimes(params long[] times) => new()
    {
        Actions = times.Select((time, index) => new MacroAction
        {
            AtMs = time,
            Type = index % 2 == 0 ? MacroActionType.KeyDown : MacroActionType.KeyUp,
            KeyCode = 65
        }).ToList()
    };
}
