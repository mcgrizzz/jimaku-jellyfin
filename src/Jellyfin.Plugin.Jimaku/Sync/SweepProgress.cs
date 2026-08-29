using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;

namespace Jellyfin.Plugin.Jimaku.Sync;

/// <summary>
/// What happened to one episode during a sweep.
/// </summary>
/// <param name="EpisodeId">The episode.</param>
/// <param name="Name">A display name for it.</param>
/// <param name="Applied">Whether a subtitle was attached.</param>
/// <param name="Verdict">The verdict reached.</param>
/// <param name="FileName">The file that was used, when one was.</param>
/// <param name="Message">The explanation.</param>
/// <param name="FinishedUtc">When it finished.</param>
public readonly record struct SweepOutcome(
    Guid EpisodeId,
    string Name,
    bool Applied,
    string Verdict,
    string FileName,
    string Message,
    DateTimeOffset FinishedUtc);

/// <summary>
/// Live state of the running sweep.
/// </summary>
/// <remarks>
/// Jellyfin's Scheduled Tasks view renders a task's name and a percentage, and nothing else - not
/// its description, and certainly not what it is currently working on. So a sweep of a few hundred
/// episodes was a number ticking upwards with the log as the only way to see what it had actually
/// done. This is the state behind both halves of the fix: a live panel on the plugin's own page,
/// and the running commentary folded into the task's name, which is the one field the built-in
/// view does render.
/// </remarks>
public sealed class SweepProgress
{
    private const int MaxOutcomes = 200;

    private readonly ConcurrentQueue<SweepOutcome> _outcomes = new();
    private readonly Lock _gate = new();

    private CancellationTokenSource? _cancellation;

    /// <summary>Gets a value indicating whether a sweep is running.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Gets what the running sweep covers, for display.</summary>
    public string Scope { get; private set; } = string.Empty;

    /// <summary>Gets the episode currently being worked on.</summary>
    public string CurrentEpisode { get; private set; } = string.Empty;

    /// <summary>Gets how many episodes have been dealt with.</summary>
    public int Completed { get; private set; }

    /// <summary>Gets how many episodes the run covers.</summary>
    public int Total { get; private set; }

    /// <summary>Gets how many subtitles were attached.</summary>
    public int Applied { get; private set; }

    /// <summary>Gets how many episodes were declined.</summary>
    public int Declined { get; private set; }

    /// <summary>Gets how many episodes were skipped without being attempted.</summary>
    public int Skipped { get; private set; }

    /// <summary>Gets when the run started.</summary>
    public DateTimeOffset? StartedUtc { get; private set; }

    /// <summary>Gets when the run finished, when it has.</summary>
    public DateTimeOffset? FinishedUtc { get; private set; }

    /// <summary>Gets how the run ended.</summary>
    public string Conclusion { get; private set; } = string.Empty;

    /// <summary>Gets the outcomes so far, newest first.</summary>
    public IReadOnlyList<SweepOutcome> Outcomes => _outcomes.Reverse().ToList();

    /// <summary>Gets the fraction complete, between 0 and 1.</summary>
    public double Fraction => Total > 0 ? Math.Min(1.0, (double)Completed / Total) : 0;

    /// <summary>
    /// Begins a run, if none is already going.
    /// </summary>
    /// <param name="scope">What the run covers, for display.</param>
    /// <param name="total">How many episodes it covers.</param>
    /// <param name="cancellation">The source that cancels it.</param>
    /// <returns><see langword="false"/> when a sweep is already running.</returns>
    public bool TryBegin(string scope, int total, CancellationTokenSource cancellation)
    {
        lock (_gate)
        {
            if (IsRunning)
            {
                return false;
            }

            IsRunning = true;
            Scope = scope;
            Total = total;
            Completed = 0;
            Applied = 0;
            Declined = 0;
            Skipped = 0;
            CurrentEpisode = string.Empty;
            Conclusion = string.Empty;
            StartedUtc = DateTimeOffset.UtcNow;
            FinishedUtc = null;
            _cancellation = cancellation;
            _outcomes.Clear();
            return true;
        }
    }

    /// <summary>Notes which episode is being worked on.</summary>
    /// <param name="name">A display name.</param>
    public void SetCurrent(string name)
    {
        lock (_gate)
        {
            CurrentEpisode = name;
        }
    }

    /// <summary>Records an episode's outcome.</summary>
    /// <param name="outcome">What happened.</param>
    public void Record(SweepOutcome outcome)
    {
        lock (_gate)
        {
            Completed++;
            if (outcome.Applied)
            {
                Applied++;
            }
            else
            {
                Declined++;
            }
        }

        _outcomes.Enqueue(outcome);
        while (_outcomes.Count > MaxOutcomes && _outcomes.TryDequeue(out _))
        {
            // Bounded: this is a live view, not a permanent record. The history store is that.
        }
    }

    /// <summary>Notes an episode that was passed over without being attempted.</summary>
    public void RecordSkip()
    {
        lock (_gate)
        {
            Completed++;
            Skipped++;
        }
    }

    /// <summary>Ends the run.</summary>
    /// <param name="conclusion">How it ended.</param>
    public void Finish(string conclusion)
    {
        lock (_gate)
        {
            IsRunning = false;
            CurrentEpisode = string.Empty;
            Conclusion = conclusion;
            FinishedUtc = DateTimeOffset.UtcNow;
            _cancellation = null;
        }
    }

    /// <summary>Asks the running sweep to stop.</summary>
    /// <returns><see langword="true"/> when a run was asked to stop.</returns>
    public bool Cancel()
    {
        lock (_gate)
        {
            if (!IsRunning || _cancellation is null)
            {
                return false;
            }

            _cancellation.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Builds the running commentary shown in Jellyfin's Scheduled Tasks list.
    /// </summary>
    /// <remarks>
    /// That view renders a task's name and a percentage, and nothing else, so the name is the only
    /// place a sweep can say what it is doing. It reverts to the plain name the moment the run ends.
    /// </remarks>
    /// <param name="baseName">The task's ordinary name.</param>
    /// <returns>The name to display.</returns>
    public string DescribeFor(string baseName)
    {
        lock (_gate)
        {
            if (!IsRunning || Total == 0)
            {
                return baseName;
            }

            var counts = Applied > 0
                ? string.Create(CultureInfo.InvariantCulture, $", {Applied} attached")
                : string.Empty;

            var where = string.IsNullOrEmpty(CurrentEpisode) ? string.Empty : " - " + CurrentEpisode;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{baseName}{where} ({Completed + 1} of {Total}{counts})");
        }
    }
}
