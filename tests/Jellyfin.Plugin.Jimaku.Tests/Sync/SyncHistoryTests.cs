using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Jimaku.Sync;
using Jellyfin.Plugin.Jimaku.Timing;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Sync;

/// <summary>
/// What the plugin remembers about an episode after the fact.
/// </summary>
/// <remarks>
/// The sidecar's filename is derived from the media file, so nothing on disk records which Jimaku
/// upload produced it. Without a durable record there is no answer to "what is attached, and what
/// did I already reject" - and automatic selection re-picks a file the user has already thrown
/// away, every single run.
/// </remarks>
public sealed class SyncHistoryTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "jimaku-history-" + Guid.NewGuid().ToString("N"));

    private readonly SyncHistoryStore _store;
    private readonly Guid _item = Guid.NewGuid();

    public SyncHistoryTests()
    {
        _store = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static SyncAttempt Applied(string fileName, string group = "AnimeOut", string path = "/media/ep.jpn.ass") => new()
    {
        AttemptedUtc = DateTimeOffset.UtcNow,
        Verdict = SyncVerdict.ConstantOffset,
        Status = AttemptStatus.Applied,
        FileName = fileName,
        ReleaseGroup = group,
        SidecarPath = path,
        EntryId = 42,
    };

    [Fact]
    public async Task ANewAttemptSupersedesTheOneBeforeItRatherThanErasingIt()
    {
        await _store.RecordAttemptAsync(_item, Applied("first.ass", path: "/media/ep.jpn.ass"), CancellationToken.None);
        await _store.RecordAttemptAsync(_item, Applied("second.ass", path: "/media/ep.1.jpn.ass"), CancellationToken.None);

        var entry = _store.Get(_item)!;

        Assert.Equal(2, entry.Attempts.Count);
        Assert.Equal(AttemptStatus.Superseded, entry.Attempts[0].Status);
        Assert.Equal(AttemptStatus.Applied, entry.Attempts[1].Status);

        // "What did I already try" is the question the filesystem cannot answer.
        Assert.Equal("first.ass", entry.Attempts[0].FileName);
    }

    [Fact]
    public async Task OnlyTheCurrentSidecarIsOfferedForRemoval()
    {
        await _store.RecordAttemptAsync(_item, Applied("first.ass", path: "/media/a.jpn.ass"), CancellationToken.None);

        Assert.Equal(["/media/a.jpn.ass"], _store.AppliedSidecarPaths(_item));

        await _store.RecordAttemptAsync(_item, Applied("second.ass", path: "/media/b.jpn.ass"), CancellationToken.None);

        // The superseded path is history, not a deletion target: it was already removed when the
        // replacement was written.
        Assert.Equal(["/media/b.jpn.ass"], _store.AppliedSidecarPaths(_item));
    }

    [Fact]
    public async Task ADeclineIsRecordedWithoutDisturbingWhatIsAttached()
    {
        await _store.RecordAttemptAsync(_item, Applied("good.ass"), CancellationToken.None);
        await _store.RecordAttemptAsync(
            _item,
            new SyncAttempt { Status = AttemptStatus.Declined, Verdict = SyncVerdict.Declined, Reason = "no reference" },
            CancellationToken.None);

        var entry = _store.Get(_item)!;

        Assert.Equal(AttemptStatus.Applied, entry.Attempts[0].Status);
        Assert.Single(_store.AppliedSidecarPaths(_item));
    }

    [Fact]
    public async Task DeletingTheFileIsReadAsARejection()
    {
        await _store.RecordAttemptAsync(_item, Applied("bad.ass"), CancellationToken.None);

        // Still there: nothing to conclude.
        Assert.Null(await _store.NoteDeletionAsync(_item, stillPresent: true, CancellationToken.None));

        var rejected = await _store.NoteDeletionAsync(_item, stillPresent: false, CancellationToken.None);

        Assert.NotNull(rejected);
        Assert.Equal("bad.ass", rejected.FileName);
        Assert.Contains("bad.ass", _store.RejectedFileNames(_item));
        Assert.Empty(_store.AppliedSidecarPaths(_item));
    }

    [Fact]
    public async Task ARejectionIsNotedOnlyOnce()
    {
        await _store.RecordAttemptAsync(_item, Applied("bad.ass"), CancellationToken.None);
        await _store.NoteDeletionAsync(_item, stillPresent: false, CancellationToken.None);

        // A second sweep over an episode with nothing attached must not keep finding rejections.
        Assert.Null(await _store.NoteDeletionAsync(_item, stillPresent: false, CancellationToken.None));
        Assert.Single(_store.Get(_item)!.RejectedFileNames);
    }

    [Fact]
    public async Task RejectionsSurviveBeingReadBackFromDisk()
    {
        await _store.RecordAttemptAsync(_item, Applied("bad.ass"), CancellationToken.None);
        await _store.RejectCurrentAsync(_item, CancellationToken.None);

        // A fresh store, as after a server restart: the record has to be on disk, not in memory.
        var reopened = new SyncHistoryStore(NullLogger<SyncHistoryStore>.Instance) { Directory = _directory };

        Assert.Contains("bad.ass", reopened.RejectedFileNames(_item));
        Assert.Equal(AttemptStatus.Rejected, reopened.Get(_item)!.Attempts.Single().Status);
    }

    [Fact]
    public async Task RejectionsCanBeTakenBack()
    {
        await _store.RecordAttemptAsync(_item, Applied("bad.ass"), CancellationToken.None);
        await _store.RejectCurrentAsync(_item, CancellationToken.None);
        await _store.ClearRejectionsAsync(_item, CancellationToken.None);

        Assert.Empty(_store.RejectedFileNames(_item));
    }

    [Fact]
    public async Task NothingAttachedMeansNothingToReject()
    {
        Assert.Null(await _store.RejectCurrentAsync(_item, CancellationToken.None));
    }

    [Fact]
    public async Task TheLogIsBounded()
    {
        for (var i = 0; i < 40; i++)
        {
            await _store.RecordAttemptAsync(
                _item,
                new SyncAttempt { Status = AttemptStatus.Declined, FileName = $"try{i}.ass" },
                CancellationToken.None);
        }

        var entry = _store.Get(_item)!;

        Assert.Equal(20, entry.Attempts.Count);
        Assert.Equal("try39.ass", entry.Attempts[^1].FileName);
    }

    [Fact]
    public async Task AnOlderRecordWithNoAttemptListStillLoads()
    {
        // Versions before this shipped wrote only the flat "last attempt" fields. Those files are
        // sitting on the user's server right now and must not throw on read.
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(
            Path.Combine(_directory, _item.ToString("N") + ".json"),
            """{"attemptedUtc":"2026-01-01T00:00:00+00:00","verdict":1,"fileName":"old.ass","sidecarPath":"/media/old.jpn.ass"}""",
            CancellationToken.None);

        var entry = _store.Get(_item);

        Assert.NotNull(entry);
        Assert.Equal("old.ass", entry.FileName);
        Assert.Empty(entry.Attempts);
        Assert.Empty(entry.RejectedFileNames);
    }
}
