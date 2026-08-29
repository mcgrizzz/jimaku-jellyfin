using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.Jimaku.Timing;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Activity;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// Tells the user what happened, through the two channels a server plugin actually has.
/// </summary>
/// <remarks>
/// <para>
/// Jellyfin's own subtitle dialog gives no feedback: it says "download queued" some seconds after
/// the tap, then nothing, and the track appears on a later refresh with no indication that it ever
/// finished. A plugin cannot fix that dialog - core owns it, and there is no progress channel to
/// hook. What a plugin can do is speak for itself afterwards, and there are exactly two ways:
/// a transient toast pushed to a client session, and a durable line in Dashboard - Activity.
/// </para>
/// <para>
/// Both are resolved on use rather than injected. Every <see cref="MediaBrowser.Controller.Subtitles.ISubtitleProvider"/>
/// is constructed while the container is still assembling the provider manager, so a constructor
/// dependency here would be taken mid-graph - the same shape as the cycle that once stopped the
/// server booting.
/// </para>
/// </remarks>
public sealed class SyncNotifier(IServiceProvider serviceProvider, ILogger<SyncNotifier> logger)
{
    /// <summary>Announces the outcome of one episode's sync.</summary>
    /// <param name="episode">The episode.</param>
    /// <param name="result">What happened.</param>
    /// <param name="interactive">
    /// Whether a person asked for this. An unattended sweep only ever notifies a session that is
    /// playing the very episode it just fixed; an interactive request also reaches whoever was
    /// recently active, since that is almost certainly the person who asked.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing the notification.</returns>
    public async Task NotifyAsync(
        Episode episode,
        SyncResult result,
        bool interactive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(episode);
        ArgumentNullException.ThrowIfNull(result);

        var configuration = Plugin.Instance?.Configuration;
        if (configuration is null)
        {
            return;
        }

        // A sweep over a large library declines most of what it looks at, because most episodes
        // genuinely have no matching subtitle on Jimaku. Recording every one of those would bury
        // the activity feed in non-events. Declines are still written to the plugin's own history
        // and the server log, where looking for them is a deliberate act.
        if (configuration.WriteActivityLog && (result.Applied || interactive))
        {
            await WriteActivityAsync(episode, result, cancellationToken).ConfigureAwait(false);
        }

        if (configuration.ShowClientNotifications)
        {
            await ToastAsync(episode, result, interactive, configuration.NotifyRecentMinutes, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Builds the one-line summary shown in a toast and in the activity feed.</summary>
    /// <param name="result">The outcome to describe.</param>
    /// <returns>A short sentence.</returns>
    public static string Summarize(SyncResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.Applied)
        {
            return "No Japanese subtitle could be verified.";
        }

        var file = result.FileName ?? "a subtitle";

        return result.Verdict switch
        {
            SyncVerdict.Exact => $"Attached {file}, already in sync.",
            SyncVerdict.ConstantOffset => string.Create(
                CultureInfo.InvariantCulture,
                $"Attached {file}, shifted {result.Transform.OffsetSeconds:+0.000;-0.000}s."),
            SyncVerdict.FramerateDrift => string.Create(
                CultureInfo.InvariantCulture,
                $"Attached {file}, corrected for framerate drift (x{result.Transform.Scale:0.######})."),
            SyncVerdict.PiecewiseCut => $"Attached {file}, re-timed across a differing cut.",
            _ => $"Attached {file}.",
        };
    }

    private static string Describe(Episode episode)
    {
        var series = episode.SeriesName;
        var number = episode.ParentIndexNumber.HasValue && episode.IndexNumber.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"S{episode.ParentIndexNumber:00}E{episode.IndexNumber:00}")
            : null;

        var parts = new[] { series, number, episode.Name }
            .Where(p => !string.IsNullOrWhiteSpace(p));

        var joined = string.Join(" ", parts);
        return joined.Length > 0 ? joined : "an episode";
    }

    private async Task WriteActivityAsync(Episode episode, SyncResult result, CancellationToken cancellationToken)
    {
        try
        {
            var activity = serviceProvider.GetService<IActivityManager>();
            if (activity is null)
            {
                return;
            }

            var entry = new ActivityLog(
                result.Applied
                    ? $"Japanese subtitles attached to {Describe(episode)}"
                    : $"No Japanese subtitles found for {Describe(episode)}",
                "JimakuSubtitleSync",
                Guid.Empty)
            {
                Overview = result.Message,
                ShortOverview = Summarize(result),
                ItemId = episode.Id.ToString("N", CultureInfo.InvariantCulture),
                LogSeverity = result.Applied ? LogLevel.Information : LogLevel.Warning,
            };

            await activity.CreateAsync(entry).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Reporting an outcome must never be able to change it.
            logger.LogDebug(ex, "Could not write an activity log entry for {Name}.", episode.Name);
        }
    }

    private async Task ToastAsync(
        Episode episode,
        SyncResult result,
        bool interactive,
        int recentMinutes,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionInfo> targets;
        ISessionManager sessions;

        try
        {
            var manager = serviceProvider.GetService<ISessionManager>();
            if (manager is null)
            {
                return;
            }

            sessions = manager;
            targets = SelectTargets(manager.Sessions, episode.Id, interactive, recentMinutes);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not enumerate sessions to notify.");
            return;
        }

        if (targets.Count == 0)
        {
            return;
        }

        var command = new MessageCommand
        {
            Header = "Jimaku",
            Text = Describe(episode) + " - " + Summarize(result),
            TimeoutMs = 8000,
        };

        foreach (var session in targets)
        {
            try
            {
                // An empty controlling session ID is what tells core there is no remote-control
                // relationship to authorize here: this is the server talking to a client, not one
                // user driving another's session.
                await sessions.SendMessageCommand(string.Empty, session.Id, command, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not notify session {Session}.", session.Id);
            }
        }
    }

    /// <summary>
    /// Picks the sessions that should hear about this.
    /// </summary>
    /// <remarks>
    /// There is no user identity to work from: the native subtitle flow reaches the plugin through
    /// core's subtitle manager, which carries no caller. So this reasons about attention instead.
    /// A session playing the very episode that just changed is unambiguously interested. Failing
    /// that, an interactive request came from someone who is at a screen right now, which the
    /// recent-activity window approximates.
    /// </remarks>
    /// <param name="sessions">The live sessions.</param>
    /// <param name="episodeId">The episode that changed.</param>
    /// <param name="interactive">Whether a person asked for this.</param>
    /// <param name="recentMinutes">How recently a session must have been active to count.</param>
    /// <returns>The sessions to notify.</returns>
    internal static IReadOnlyList<SessionInfo> SelectTargets(
        IEnumerable<SessionInfo> sessions,
        Guid episodeId,
        bool interactive,
        int recentMinutes)
    {
        var capable = sessions
            .Where(s => s.IsActive)
            .Where(s => s.SupportedCommands.Contains(GeneralCommandType.DisplayMessage))
            .ToList();

        var watching = capable
            .Where(s => s.FullNowPlayingItem?.Id == episodeId
                     || s.NowPlayingItem?.Id == episodeId
                     || s.NowViewingItem?.Id == episodeId)
            .ToList();

        if (watching.Count > 0 || !interactive)
        {
            return watching;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(Math.Max(1, recentMinutes));
        return capable
            .Where(s => s.UserId != Guid.Empty && s.LastActivityDate >= cutoff)
            .ToList();
    }
}
