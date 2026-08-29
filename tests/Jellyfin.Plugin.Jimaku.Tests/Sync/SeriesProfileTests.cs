using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Jellyfin.Plugin.Jimaku.Sync;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// The per-series release-group memory.
/// </summary>
/// <remarks>
/// The point of the vote is hysteresis. A preference that flipped on every disagreement would be
/// no better than deciding each episode from scratch, which is the behaviour this replaced; one
/// that never flipped would entrench the first answer forever. These pin both ends.
/// </remarks>
public class SeriesProfileTests
{
    private const int MinConfirmations = 3;

    [Fact]
    public void FirstSuccess_EstablishesThePreference()
    {
        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);

        Assert.Equal("AnimeOut", profile.PreferredReleaseGroup);
        Assert.Equal(1, profile.Confirmations);
        Assert.Equal(100, profile.PreferredEntryId);
    }

    [Fact]
    public void PreferenceIsNotUsedUntilItHasEnoughSupport()
    {
        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);

        Assert.False(SeriesProfileStore.IsPreferred(profile, "AnimeOut", 100, MinConfirmations));

        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);

        Assert.True(SeriesProfileStore.IsPreferred(profile, "AnimeOut", 100, MinConfirmations));
        Assert.False(SeriesProfileStore.IsPreferred(profile, "Nekomoe kissaten", 100, MinConfirmations));
    }

    [Fact]
    public void OneDisagreementDoesNotOverturnASeasonOfAgreement()
    {
        var profile = new SeriesProfile();
        for (var i = 0; i < 5; i++)
        {
            SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        }

        SeriesProfileStore.RecordSuccess(profile, "Nekomoe kissaten", 200);

        Assert.Equal("AnimeOut", profile.PreferredReleaseGroup);
        Assert.True(SeriesProfileStore.IsPreferred(profile, "AnimeOut", 100, MinConfirmations));
    }

    [Fact]
    public void SustainedDisagreementDoesOverturnIt()
    {
        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);

        // Two contrary results spend the two confirmations; the third takes the seat.
        SeriesProfileStore.RecordSuccess(profile, "Nekomoe kissaten", 200);
        SeriesProfileStore.RecordSuccess(profile, "Nekomoe kissaten", 200);
        SeriesProfileStore.RecordSuccess(profile, "Nekomoe kissaten", 200);

        Assert.Equal("Nekomoe kissaten", profile.PreferredReleaseGroup);
        Assert.Equal(200, profile.PreferredEntryId);
    }

    [Fact]
    public void SupportIsCapped_SoAStalePreferenceCanStillBeUnseated()
    {
        var profile = new SeriesProfile();
        for (var i = 0; i < 50; i++)
        {
            SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        }

        // Without a cap this would take fifty contrary episodes rather than eleven.
        for (var i = 0; i < 11; i++)
        {
            SeriesProfileStore.RecordSuccess(profile, "Erai-raws", 300);
        }

        Assert.Equal("Erai-raws", profile.PreferredReleaseGroup);
    }

    [Fact]
    public void ComparisonIgnoresCase()
    {
        var profile = new SeriesProfile();
        for (var i = 0; i < MinConfirmations; i++)
        {
            SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        }

        Assert.True(SeriesProfileStore.IsPreferred(profile, "animeout", 100, MinConfirmations));
    }

    [Fact]
    public void AnUntaggedFileFallsBackToTheEntryItCameFrom()
    {
        var profile = new SeriesProfile();
        for (var i = 0; i < MinConfirmations; i++)
        {
            SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        }

        // Plenty of Jimaku uploads are named "01.ja.srt" and parse to no group at all. The entry
        // is the only signal left, and it is only consulted in exactly that case.
        Assert.True(SeriesProfileStore.IsPreferred(profile, null, 100, MinConfirmations));
        Assert.False(SeriesProfileStore.IsPreferred(profile, null, 999, MinConfirmations));
    }

    [Fact]
    public void NoProfileMeansNoPreference()
    {
        Assert.False(SeriesProfileStore.IsPreferred(null, "AnimeOut", 100, MinConfirmations));
    }

    [Fact]
    public void AnUngroupedSuccessDoesNotClaimTheSeat()
    {
        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, null, 100);

        Assert.Equal(string.Empty, profile.PreferredReleaseGroup);
        Assert.Equal(0, profile.Confirmations);
        Assert.Equal(100, profile.PreferredEntryId);

        // ...and it must not displace a group that has actually been established.
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 200);
        SeriesProfileStore.RecordSuccess(profile, null, 999);

        Assert.Equal("AnimeOut", profile.PreferredReleaseGroup);
        Assert.Equal(200, profile.PreferredEntryId);
    }

    [Fact]
    public async Task EntriesCache_RespectsTheLookupFingerprintAndTheTtl()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jimaku-series-" + Guid.NewGuid().ToString("N"));
        var store = new SeriesProfileStore(NullLogger<SeriesProfileStore>.Instance) { Directory = directory };
        var series = Guid.NewGuid();

        try
        {
            await store.RememberEntriesAsync(
                series,
                "a=12345;t=;q=",
                [new SeriesEntry { Id = 7, Name = "Mushoku Tensei" }],
                CancellationToken.None);

            Assert.NotNull(store.GetEntries(series, "a=12345;t=;q=", 12));

            // A re-scrape onto a different ID must not silently reuse the old series' entries.
            Assert.Null(store.GetEntries(series, "a=99999;t=;q=", 12));

            // Zero hours disables the cache outright.
            Assert.Null(store.GetEntries(series, "a=12345;t=;q=", 0));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
