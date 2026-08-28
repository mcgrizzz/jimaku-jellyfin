namespace Jellyfin.Plugin.Jimaku.Timing;

/// <summary>Subtitle container formats this plugin can retime.</summary>
public enum SubtitleFormatKind
{
    /// <summary>Unrecognized or unparseable.</summary>
    Unknown = 0,

    /// <summary>Advanced SubStation Alpha (.ass) or SubStation Alpha (.ssa).</summary>
    Ass = 1,

    /// <summary>SubRip (.srt).</summary>
    Srt = 2,
}
