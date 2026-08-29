using Jellyfin.Plugin.Jimaku.Configuration;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// What the series preference is allowed to learn from.
/// </summary>
/// <remarks>
/// The failure this guards against is a feedback loop rather than a crash, which is why it is
/// pinned rather than left to review. If an automatic pick could confirm the preference that
/// produced it, whatever the first sweep landed on would bias the second episode, confirm itself
/// again, and inside a season a coin flip would have hardened into a rule that then outranks
/// measurement everywhere. Nothing would look wrong in the logs.
/// </remarks>
public class LearningEvidenceTests
{
    private static readonly PluginConfiguration Enabled = new() { UseSeriesPreference = true };

    private static SyncResult Applied() => new() { Applied = true, Verdict = SyncVerdict.Exact };

    private static SyncOptions Chosen() => new()
    {
        ForcedFile = new JimakuFile { Name = "[AnimeOut] Show - 09.ass" },
        ForcedEntryId = 42,
    };

    [Fact]
    public void APickTheUserMadeCounts()
    {
        Assert.True(JimakuSyncService.ShouldLearnFrom(Applied(), Chosen(), Enabled));
    }

    [Fact]
    public void APickThePluginMadeDoesNot()
    {
        // The Auto button, and every episode of a scheduled or bulk sweep.
        Assert.False(JimakuSyncService.ShouldLearnFrom(Applied(), new SyncOptions(), Enabled));
    }

    [Fact]
    public void ABulkRunOverAWholeSeasonCannotPromoteItsOwnFirstChoice()
    {
        var bulk = new SyncOptions { Interactive = false };

        for (var episode = 0; episode < 12; episode++)
        {
            Assert.False(JimakuSyncService.ShouldLearnFrom(Applied(), bulk, Enabled));
        }
    }

    [Fact]
    public void NothingIsLearnedWhenNothingWasAttached()
    {
        Assert.False(JimakuSyncService.ShouldLearnFrom(SyncResult.Fail("declined"), Chosen(), Enabled));
    }

    [Fact]
    public void NothingIsLearnedWhenThePreferenceIsTurnedOff()
    {
        var off = new PluginConfiguration { UseSeriesPreference = false };

        Assert.False(JimakuSyncService.ShouldLearnFrom(Applied(), Chosen(), off));
    }
}
