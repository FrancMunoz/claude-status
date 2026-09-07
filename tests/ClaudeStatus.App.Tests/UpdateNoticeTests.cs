using ClaudeStatus.App.ViewModels;
using ClaudeStatus.Localization;
using ClaudeStatus.Update;
using Microsoft.Extensions.Time.Testing;

namespace ClaudeStatus.App.Tests;

/// <summary>
/// What the popup says about a pending update, and when it says nothing.
/// </summary>
/// <remarks>
/// The updater runs unprompted in the background. Almost all of its states are
/// the app's business rather than the user's, so most of these tests assert that
/// nothing is shown - which is the behaviour most likely to regress, because it
/// is the behaviour nobody sees working.
/// </remarks>
public class UpdateNoticeTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;
    private static readonly Localizer L = TestLocalizer.English();

    private static DetailsViewModel Build()
    {
        var clock = new FakeTimeProvider(Now);
        var monitor = new UsageMonitor(
            new FakeUsageProvider(FakeUsageScenario.Healthy, clock), new PollingOptions(), clock);

        return new DetailsViewModel(monitor, () => new AppSettings(), L, clock);
    }

    [Theory]
    [InlineData(UpdateState.Unsupported)]
    [InlineData(UpdateState.Idle)]
    [InlineData(UpdateState.Checking)]
    [InlineData(UpdateState.Downloading)]
    [InlineData(UpdateState.Failed)]
    public void Everything_short_of_ready_stays_out_of_the_way(UpdateState state)
    {
        // Checking and downloading are progress on something the user did not ask
        // for. Failed is the important one: reporting it would put a warning about
        // our own release infrastructure in front of someone who opened the window
        // to look at a percentage, and the next check retries anyway.
        using DetailsViewModel model = Build();

        model.ApplyUpdate(new UpdateStatus(state, "9.9.9"));

        model.HasUpdate.Should().BeFalse();
        model.UpdateText.Should().BeEmpty();
    }

    [Fact]
    public void A_staged_update_is_announced_with_its_version()
    {
        using DetailsViewModel model = Build();

        model.ApplyUpdate(new UpdateStatus(UpdateState.ReadyToApply, "1.4.2"));

        model.HasUpdate.Should().BeTrue();
        model.UpdateText.Should().Contain("1.4.2");
    }

    [Fact]
    public void The_notice_says_it_installs_by_itself()
    {
        // The offer is a shortcut, not an instruction: the update applies at the
        // next start whether or not anybody clicks anything. Saying so is what
        // makes ignoring it a reasonable choice rather than a deferred chore.
        using DetailsViewModel model = Build();

        model.ApplyUpdate(new UpdateStatus(UpdateState.ReadyToApply, "1.4.2"));

        model.UpdateText.Should().Contain("next time");
    }

    [Fact]
    public void The_notice_clears_when_the_state_goes_back()
    {
        using DetailsViewModel model = Build();

        model.ApplyUpdate(new UpdateStatus(UpdateState.ReadyToApply, "1.4.2"));
        model.ApplyUpdate(new UpdateStatus(UpdateState.Idle));

        model.HasUpdate.Should().BeFalse();
        model.UpdateText.Should().BeEmpty();
    }

    [Fact]
    public void Restarting_asks_the_controller_rather_than_doing_it()
    {
        // Applying an update replaces the process. A view model has no business
        // knowing that, so it raises an event and the controller acts.
        using DetailsViewModel model = Build();

        int raised = 0;
        model.UpdateRequested += (_, _) => raised++;
        model.ApplyUpdateNowCommand.Execute(null);

        raised.Should().Be(1);
    }

    [Fact]
    public void An_uninstallable_build_reports_nothing_and_does_nothing()
    {
        // What a `dotnet run` build, and the portable zip, both get. It has to be
        // inert rather than absent so no caller needs a null check.
        using var service = new NullUpdateService();

        service.Status.State.Should().Be(UpdateState.Unsupported);
        service.Status.IsNoteworthy.Should().BeFalse();

        // Neither of these may throw, and neither may reach the network.
        service.CheckAsync(TestContext.Current.CancellationToken)
            .IsCompletedSuccessfully.Should().BeTrue();
        service.ApplyAndRestart();
    }

    [Theory]
    [InlineData(UpdateState.Downloading, true)]
    [InlineData(UpdateState.ReadyToApply, true)]
    [InlineData(UpdateState.Idle, false)]
    [InlineData(UpdateState.Failed, false)]
    [InlineData(UpdateState.Unsupported, false)]
    public void Noteworthy_means_something_is_actually_happening(UpdateState state, bool expected)
    {
        new UpdateStatus(state).IsNoteworthy.Should().Be(expected);
    }
}
