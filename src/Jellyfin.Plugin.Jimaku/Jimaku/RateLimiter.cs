using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Jimaku.Jimaku;

/// <summary>
/// A sliding-window rate limiter that delays callers rather than rejecting them.
/// </summary>
/// <remarks>
/// Jimaku allows 25 requests per 60 seconds per IP, applied across the whole API. A library sweep
/// will blow through that in a second or two, so the limit has to be respected proactively; waiting
/// for a 429 and backing off wastes requests and, on a big library, never converges.
/// </remarks>
public sealed class RateLimiter : IDisposable
{
    private readonly int _permits;
    private readonly TimeSpan _window;
    private readonly Queue<DateTimeOffset> _timestamps = new();
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly TimeProvider _timeProvider;

    private DateTimeOffset _blockedUntil = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="RateLimiter"/> class.</summary>
    /// <param name="permits">Requests allowed per window.</param>
    /// <param name="window">The window length.</param>
    /// <param name="timeProvider">Time source, for testing.</param>
    public RateLimiter(int permits = 25, TimeSpan? window = null, TimeProvider? timeProvider = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(permits);
        _permits = permits;
        _window = window ?? TimeSpan.FromSeconds(60);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Waits until another request may be sent, then records it.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the caller may proceed.</returns>
    public async Task AcquireAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            TimeSpan delay;

            await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var now = _timeProvider.GetUtcNow();

                while (_timestamps.Count > 0 && now - _timestamps.Peek() >= _window)
                {
                    _timestamps.Dequeue();
                }

                var serverDelay = _blockedUntil > now ? _blockedUntil - now : TimeSpan.Zero;
                var windowDelay = _timestamps.Count >= _permits
                    ? _window - (now - _timestamps.Peek())
                    : TimeSpan.Zero;

                delay = serverDelay > windowDelay ? serverDelay : windowDelay;

                if (delay <= TimeSpan.Zero)
                {
                    _timestamps.Enqueue(now);
                    return;
                }
            }
            finally
            {
                _mutex.Release();
            }

            await Task.Delay(delay + TimeSpan.FromMilliseconds(50), _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records a server-side rate limit, holding every caller off until it lifts.
    /// </summary>
    /// <param name="retryAfter">How long the server asked us to wait.</param>
    public void ApplyServerBackoff(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            return;
        }

        _mutex.Wait();
        try
        {
            var until = _timeProvider.GetUtcNow() + retryAfter;
            if (until > _blockedUntil)
            {
                _blockedUntil = until;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _mutex.Dispose();
}
