using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// What happens to data written by earlier versions.
/// </summary>
/// <remarks>
/// This matters more than most migrations because the data is already on a running server. An
/// episode synced before attempts were logged would otherwise be invisible to every behaviour that
/// reads the attempt list - its sidecar never cleaned up, deleting it teaching nothing, the reject
/// action claiming nothing is attached - and a release-group preference gathered when the plugin's
/// own picks counted is circular rather than merely weak.
/// </remarks>
public sealed class MigrationTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "jimaku-migrate-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private string Write(Guid id, string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, id.ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AnAppliedRecordFromBeforeAttemptsWereLoggedBecomesOne()
    {
        var id = Guid.NewGuid();
        Write(id, """
            {"attemptedUtc":"2026-08-01T00:00:00+00:00","verdict":1,
             "entryId":4321,"fileName":"[AnimeOut] Show - 09 [BD].ass",
             "sidecarPath":"/media/Show S01E09.jpn.ass","offsetSeconds":0.21,"scale":1.0,
             "correlation":0.97,"reason":"shifted"}
            """);

        var store = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };
        var entry = store.Get(id)!;
        var attempt = Assert.Single(entry.Attempts);

        Assert.Equal(AttemptStatus.Applied, attempt.Status);
        Assert.Equal("[AnimeOut] Show - 09 [BD].ass", attempt.FileName);
        Assert.Equal("AnimeOut", attempt.ReleaseGroup);
        Assert.Equal(4321, attempt.EntryId);

        // The whole point: these three now work for episodes an older version handled.
        Assert.Equal(["/media/Show S01E09.jpn.ass"], store.AppliedSidecarPaths(id));
    }

    [Fact]
    public async Task DeletingASubtitleAnOlderVersionAttachedIsStillReadAsARejection()
    {
        var id = Guid.NewGuid();
        Write(id, """
            {"attemptedUtc":"2026-08-01T00:00:00+00:00","verdict":1,
             "fileName":"[Nekomoe] Show - 09.ass","sidecarPath":"/media/Show.jpn.ass"}
            """);

        var store = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };
        var rejected = await store.NoteDeletionAsync(id, stillPresent: false, CancellationToken.None);

        Assert.NotNull(rejected);
        Assert.Equal("[Nekomoe] Show - 09.ass", rejected.FileName);
        Assert.Contains("[Nekomoe] Show - 09.ass", store.RejectedFileNames(id));
    }

    [Fact]
    public void ADeclinedRecordIsNotMistakenForAnAttachedOne()
    {
        var id = Guid.NewGuid();
        Write(id, """
            {"attemptedUtc":"2026-08-01T00:00:00+00:00","verdict":5,
             "fileName":"tried.ass","reason":"correlation too low"}
            """);

        var store = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };

        Assert.Equal(AttemptStatus.Declined, store.Get(id)!.Attempts.Single().Status);
        Assert.Empty(store.AppliedSidecarPaths(id));
    }

    [Fact]
    public void ARecordWithNoFileNameProducesNoPhantomAttempt()
    {
        var id = Guid.NewGuid();
        Write(id, """{"attemptedUtc":"2026-08-01T00:00:00+00:00","verdict":5,"reason":"nothing on Jimaku"}""");

        var store = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };

        Assert.Empty(store.Get(id)!.Attempts);
    }

    [Fact]
    public void APreferenceGatheredWhenAutomaticPicksCountedIsDiscarded()
    {
        var id = Guid.NewGuid();
        Write(id, """
            {"preferredReleaseGroup":"Nekomoe kissaten","confirmations":7,"preferredEntryId":99,
             "entriesKey":"a=12345;t=;q=","entriesCachedUtc":"2099-01-01T00:00:00+00:00",
             "entries":[{"id":7,"name":"Show"}]}
            """);

        var store = new SeriesProfileStore(NullLogger<SeriesProfileStore>.Instance) { Directory = _directory };
        var profile = store.Get(id)!;

        // Circular, not merely weak: a sweep's own choice confirmed the preference that made it.
        Assert.Equal(string.Empty, profile.PreferredReleaseGroup);
        Assert.Equal(0, profile.Confirmations);
        Assert.Equal(0, profile.PreferredEntryId);

        // The cached entry list is a fact about Jimaku, not a judgement, so it survives.
        Assert.Single(profile.Entries);
        Assert.NotNull(store.GetEntries(id, "a=12345;t=;q=", 12));
    }

    [Fact]
    public async Task APreferenceLearnedUnderTheCurrentRulesSurvivesAReload()
    {
        var id = Guid.NewGuid();
        var store = new SeriesProfileStore(NullLogger<SeriesProfileStore>.Instance) { Directory = _directory };

        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        await store.SaveAsync(id, profile, CancellationToken.None);

        var reopened = new SeriesProfileStore(NullLogger<SeriesProfileStore>.Instance) { Directory = _directory };

        Assert.Equal("AnimeOut", reopened.Get(id)!.PreferredReleaseGroup);
        Assert.Equal(2, reopened.Get(id)!.Confirmations);
    }

    [Fact]
    public async Task APreferenceCanBeForgottenOnRequest()
    {
        var id = Guid.NewGuid();
        var store = new SeriesProfileStore(NullLogger<SeriesProfileStore>.Instance) { Directory = _directory };

        var profile = new SeriesProfile();
        SeriesProfileStore.RecordSuccess(profile, "AnimeOut", 100);
        await store.SaveAsync(id, profile, CancellationToken.None);

        await store.ResetPreferenceAsync(id, CancellationToken.None);

        Assert.Equal(string.Empty, store.Get(id)!.PreferredReleaseGroup);
    }
}
