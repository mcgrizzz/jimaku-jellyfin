using System;
using System.Linq;
using System.Threading;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// The live state behind the sweep view.
/// </summary>
/// <remarks>
/// Jellyfin's Scheduled Tasks list renders a task's name and a percentage and nothing else - not
/// its description - so the running commentary has to ride on the name, and it has to revert
/// cleanly when the run ends or the task is left permanently mislabelled.
/// </remarks>
public class SweepProgressTests
{
    private const string BaseName = "Fetch Japanese subtitles from Jimaku";

    private static SweepOutcome Outcome(bool applied) =>
        new(Guid.NewGuid(), "Show S01E01", applied, applied ? "Exact" : "Declined", "f.ass", "m", DateTimeOffset.UtcNow);

    [Fact]
    public void AnIdleTaskKeepsItsOrdinaryName()
    {
        Assert.Equal(BaseName, new SweepProgress().DescribeFor(BaseName));
    }

    [Fact]
    public void ARunningTaskSaysWhereItIs()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        progress.TryBegin("Anime", 240, cancellation);
        progress.SetCurrent("Mushoku Tensei S01E09");

        var name = progress.DescribeFor(BaseName);

        Assert.Contains("Mushoku Tensei S01E09", name, StringComparison.Ordinal);
        Assert.Contains("1 of 240", name, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNameRevertsWhenTheRunEnds()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        progress.TryBegin("Anime", 10, cancellation);
        progress.SetCurrent("Something");
        progress.Finish("done");

        Assert.Equal(BaseName, progress.DescribeFor(BaseName));
    }

    [Fact]
    public void OnlyOneSweepRunsAtATime()
    {
        var progress = new SweepProgress();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        Assert.True(progress.TryBegin("Anime", 10, first));

        // Jimaku's budget is per-IP and shared, so a second run would only take turns waiting on
        // the same limiter while making the reporting incoherent.
        Assert.False(progress.TryBegin("Shows", 10, second));

        progress.Finish("done");
        Assert.True(progress.TryBegin("Shows", 10, second));
    }

    [Fact]
    public void CountsAndFractionTrackWhatHappened()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        progress.TryBegin("Anime", 4, cancellation);
        progress.Record(Outcome(applied: true));
        progress.Record(Outcome(applied: false));
        progress.RecordSkip();

        Assert.Equal(1, progress.Applied);
        Assert.Equal(1, progress.Declined);
        Assert.Equal(1, progress.Skipped);
        Assert.Equal(3, progress.Completed);
        Assert.Equal(0.75, progress.Fraction, 3);
    }

    [Fact]
    public void CancellingReachesTheRunningToken()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        Assert.False(progress.Cancel());

        progress.TryBegin("Anime", 10, cancellation);

        Assert.True(progress.Cancel());
        Assert.True(cancellation.IsCancellationRequested);
    }

    [Fact]
    public void OutcomesAreNewestFirstAndBounded()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        progress.TryBegin("Anime", 500, cancellation);
        for (var i = 0; i < 250; i++)
        {
            progress.Record(new SweepOutcome(
                Guid.NewGuid(), $"ep{i}", true, "Exact", "f.ass", "m", DateTimeOffset.UtcNow));
        }

        var outcomes = progress.Outcomes;

        Assert.Equal(200, outcomes.Count);
        Assert.Equal("ep249", outcomes[0].Name);
    }

    [Fact]
    public void AFinishedRunStillReportsWhatItDid()
    {
        var progress = new SweepProgress();
        using var cancellation = new CancellationTokenSource();

        progress.TryBegin("Anime", 2, cancellation);
        progress.Record(Outcome(applied: true));
        progress.Finish("1 attached, 0 declined, 0 skipped");

        Assert.False(progress.IsRunning);
        Assert.Equal(1, progress.Applied);
        Assert.Single(progress.Outcomes);
        Assert.NotNull(progress.FinishedUtc);
    }
}
