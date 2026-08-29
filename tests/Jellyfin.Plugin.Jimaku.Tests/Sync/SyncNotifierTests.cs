using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// Who hears about a completed sync, and what they are told.
/// </summary>
/// <remarks>
/// There is no caller identity to work from: the native subtitle flow arrives through core's
/// subtitle manager, which carries no user. So targeting reasons about attention instead, and the
/// rules matter - a household server must not toast everyone every time the nightly sweep runs.
/// </remarks>
public class SyncNotifierTests
{
    private static SessionInfo Session(
        Guid? nowPlaying = null,
        bool supportsMessages = true,
        int minutesSinceActivity = 0,
        Guid? userId = null)
    {
        var session = new SessionInfo(Mock.Of<ISessionManager>(), NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId ?? Guid.NewGuid(),
            LastActivityDate = DateTime.UtcNow.AddMinutes(-minutesSinceActivity),
            Capabilities = new ClientCapabilities
            {
                SupportedCommands = supportsMessages
                    ? [GeneralCommandType.DisplayMessage]
                    : [GeneralCommandType.Play],
            },
        };

        if (nowPlaying is { } id)
        {
            session.NowPlayingItem = new BaseItemDto { Id = id };
        }

        return session;
    }

    [Fact]
    public void ASessionPlayingTheEpisodeIsAlwaysNotified()
    {
        var episode = Guid.NewGuid();
        var watching = Session(nowPlaying: episode);
        var idle = Session(minutesSinceActivity: 1);

        var targets = SyncNotifier.SelectTargets([watching, idle], episode, interactive: false, recentMinutes: 5);

        Assert.Equal([watching.Id], targets.Select(t => t.Id));
    }

    [Fact]
    public void AnUnattendedSweepNeverToastsAnUninvolvedSession()
    {
        // The 3am sweep must not wake up a phone that merely happens to be logged in.
        var targets = SyncNotifier.SelectTargets(
            [Session(minutesSinceActivity: 0)],
            Guid.NewGuid(),
            interactive: false,
            recentMinutes: 5);

        Assert.Empty(targets);
    }

    [Fact]
    public void AnInteractiveRequestReachesWhoeverIsCurrentlyAtAScreen()
    {
        var recent = Session(minutesSinceActivity: 1);
        var stale = Session(minutesSinceActivity: 90);

        var targets = SyncNotifier.SelectTargets(
            [recent, stale],
            Guid.NewGuid(),
            interactive: true,
            recentMinutes: 5);

        Assert.Equal([recent.Id], targets.Select(t => t.Id));
    }

    [Fact]
    public void SessionsThatCannotDisplayMessagesAreSkipped()
    {
        // Swiftfin and the native TV clients do not all implement DisplayMessage. Sending anyway
        // would be a silently dropped request per session per sync.
        var targets = SyncNotifier.SelectTargets(
            [Session(minutesSinceActivity: 1, supportsMessages: false)],
            Guid.NewGuid(),
            interactive: true,
            recentMinutes: 5);

        Assert.Empty(targets);
    }

    [Fact]
    public void APlayingSessionWinsOverTheRecentActivityFallback()
    {
        var episode = Guid.NewGuid();
        var watching = Session(nowPlaying: episode);
        var browsing = Session(minutesSinceActivity: 1);

        var targets = SyncNotifier.SelectTargets(
            [browsing, watching],
            episode,
            interactive: true,
            recentMinutes: 5);

        Assert.Equal([watching.Id], targets.Select(t => t.Id));
    }

    [Theory]
    [InlineData(SyncVerdict.Exact, 1.0, 0.0, "already in sync")]
    [InlineData(SyncVerdict.ConstantOffset, 1.0, 0.21, "+0.210s")]
    [InlineData(SyncVerdict.FramerateDrift, 0.999001, 1.5, "framerate drift")]
    [InlineData(SyncVerdict.PiecewiseCut, 1.0, 0.0, "differing cut")]
    public void TheSummaryStatesWhatWasActuallyDone(
        SyncVerdict verdict,
        double scale,
        double offset,
        string expected)
    {
        var summary = SyncNotifier.Summarize(new SyncResult
        {
            Applied = true,
            Verdict = verdict,
            FileName = "sub.ass",
            Transform = new TimingTransform(scale, offset),
        });

        Assert.Contains("sub.ass", summary, StringComparison.Ordinal);
        Assert.Contains(expected, summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeclineIsReportedAsOne()
    {
        var summary = SyncNotifier.Summarize(SyncResult.Fail("nothing matched"));

        Assert.DoesNotContain("Attached", summary, StringComparison.Ordinal);
    }
}
