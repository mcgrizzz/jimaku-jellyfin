using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Jimaku.Jimaku.Models;
using Jellyfin.Plugin.Jimaku.Matching;
using Xunit;

namespace Jellyfin.Plugin.Jimaku.Tests.Matching;

public class CandidateFilterTests
{
    private static JimakuFile File(string name, long size = 40_000) =>
        new() { Name = name, Size = size, Url = "https://jimaku.cc/entry/1/download/" + name };

    private static RejectionReason ReasonFor(IReadOnlyList<FilteredCandidate> results, string name) =>
        results.First(r => r.File.Name == name).Rejection;

    [Fact]
    public void Filter_RejectsUnreadableArchivesAndMachineTranslations()
    {
        var results = CandidateFilter.Filter(
            [
                File("Show - 01.ass"),
                File("Show - 01.7z"),
                File("Show - 01.rar"),
                File("Show - 01 [WhisperAI].srt"),
                File("Show - 01 (whisper).ass"),
                File("Show - 01.txt"),
                File("Show - 01.tiny.srt", size: 120),
            ],
            allowArchives: true);

        Assert.Equal(RejectionReason.None, ReasonFor(results, "Show - 01.ass"));
        Assert.Equal(RejectionReason.UnreadableArchive, ReasonFor(results, "Show - 01.7z"));
        Assert.Equal(RejectionReason.UnreadableArchive, ReasonFor(results, "Show - 01.rar"));
        Assert.Equal(RejectionReason.MachineTranslated, ReasonFor(results, "Show - 01 [WhisperAI].srt"));
        Assert.Equal(RejectionReason.MachineTranslated, ReasonFor(results, "Show - 01 (whisper).ass"));
        Assert.Equal(RejectionReason.UnsupportedExtension, ReasonFor(results, "Show - 01.txt"));
        Assert.Equal(RejectionReason.TooSmall, ReasonFor(results, "Show - 01.tiny.srt"));
    }

    [Fact]
    public void Filter_PrefersPlainSubtitlesOverArchives()
    {
        var results = CandidateFilter.Filter(
            [File("Show - 01.ass"), File("Show - 01.zip")],
            allowArchives: true);

        Assert.Equal(RejectionReason.None, ReasonFor(results, "Show - 01.ass"));
        Assert.Equal(RejectionReason.ArchiveNotNeeded, ReasonFor(results, "Show - 01.zip"));
    }

    [Fact]
    public void Filter_AcceptsAnArchiveWhenItIsTheOnlyOption()
    {
        var results = CandidateFilter.Filter([File("Season 1 subtitles.zip")], allowArchives: true);
        Assert.Equal(RejectionReason.None, ReasonFor(results, "Season 1 subtitles.zip"));
    }

    [Fact]
    public void Filter_HonoursTheArchivesDisabledSetting()
    {
        var results = CandidateFilter.Filter([File("Season 1 subtitles.zip")], allowArchives: false);
        Assert.Equal(RejectionReason.ArchivesDisabled, ReasonFor(results, "Season 1 subtitles.zip"));
    }

    [Fact]
    public void Filter_ArchiveIsNotConsideredPlainWhenTheOnlySubtitleIsMachineTranslated()
    {
        // The archive must still be reachable: the only loose file present is one we would refuse.
        var results = CandidateFilter.Filter(
            [File("Show - 01 [WhisperAI].srt"), File("Show - 01.zip")],
            allowArchives: true);

        Assert.Equal(RejectionReason.None, ReasonFor(results, "Show - 01.zip"));
    }
}
