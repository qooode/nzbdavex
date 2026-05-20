using Serilog;

namespace NzbWebDAV.Clients.Usenet.Connections;

/// <summary>
/// Tracks consecutive connection failures for an NNTP provider and temporarily
/// disables it when a failure threshold is reached, preventing a single
/// misbehaving provider from blocking the entire download pipeline.
/// <para>
/// After tripping, the provider enters a cooldown period during which it is
/// skipped. When the cooldown expires, a single probe attempt is allowed.
/// If the probe succeeds, the breaker resets. If it fails, the cooldown
/// doubles (up to a cap) and the breaker re-trips.
/// </para>
/// </summary>
public class ProviderCircuitBreaker
{
    private const int FailureThreshold = 3;
    private static readonly TimeSpan InitialCooldown = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(5);

    // Hard-failure path uses a shorter cooldown so a one-off blip doesn't park
    // a healthy provider for a full minute. If the provider really is bad, the
    // next cooldown-probe attempt will fail and the exponential doubling kicks
    // in — same recovery curve, just starting from a friendlier baseline.
    private static readonly TimeSpan HardInitialCooldown = TimeSpan.FromSeconds(30);

    private readonly string _providerName;
    private readonly object _lock = new();

    private int _consecutiveFailures;
    private long _trippedUntilMs;
    private TimeSpan _currentCooldown = InitialCooldown;

    public ProviderCircuitBreaker(string providerName)
    {
        _providerName = providerName;
    }

    public bool IsTripped
    {
        get
        {
            var trippedUntil = Volatile.Read(ref _trippedUntilMs);
            if (trippedUntil == 0) return false;
            return Environment.TickCount64 < trippedUntil;
        }
    }

    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _trippedUntilMs > 0)
                Log.Information("Provider {Provider} recovered — circuit breaker reset.", _providerName);

            _consecutiveFailures = 0;
            _trippedUntilMs = 0;
            _currentCooldown = InitialCooldown;
        }
    }

    public void RecordFailure()
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures < FailureThreshold) return;

            _trippedUntilMs = Environment.TickCount64 + (long)_currentCooldown.TotalMilliseconds;
            Log.Warning(
                "Provider {Provider} tripped after {Failures} consecutive failures. " +
                "Skipping for {Cooldown}s.",
                _providerName, _consecutiveFailures, _currentCooldown.TotalSeconds);

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }

    /// <summary>
    /// Trip the breaker immediately, bypassing the consecutive-failure threshold.
    /// Use this for failure modes where a single observation is strong evidence
    /// the provider is misbehaving — e.g. a BODY/ARTICLE read-timeout, which
    /// healthy providers never produce. Avoids paying the same N×timeout cost
    /// on the very next user request while the provider is still stalled.
    /// </summary>
    public void RecordHardFailure(string reason)
    {
        lock (_lock)
        {
            // Already tripped? Don't extend; let the existing cooldown expire
            // and the next probe decide. (Avoids hard-failures during the probe
            // window pushing the cooldown out forever.)
            if (_trippedUntilMs > 0 && Environment.TickCount64 < _trippedUntilMs)
                return;

            // Seed the cooldown from the hard baseline if we're entering a
            // fresh trip; an already-degraded provider (cooldown previously
            // doubled past the baseline) keeps its larger window.
            if (_currentCooldown < HardInitialCooldown)
                _currentCooldown = HardInitialCooldown;

            _consecutiveFailures = Math.Max(_consecutiveFailures + 1, FailureThreshold);
            _trippedUntilMs = Environment.TickCount64 + (long)_currentCooldown.TotalMilliseconds;
            Log.Warning(
                "Provider {Provider} hard-tripped ({Reason}). Skipping for {Cooldown}s.",
                _providerName, reason, _currentCooldown.TotalSeconds);

            _currentCooldown = TimeSpan.FromMilliseconds(
                Math.Min(_currentCooldown.TotalMilliseconds * 2, MaxCooldown.TotalMilliseconds));
        }
    }
}
