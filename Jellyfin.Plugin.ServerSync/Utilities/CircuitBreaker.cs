using System;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.ServerSync.Utilities;

/// <summary>
/// Counts consecutive failures and trips into an open state after a threshold,
/// suppressing operations until a cooldown elapses.
/// </summary>
public class CircuitBreaker
{
    private readonly ILogger _logger;
    private readonly string _serviceName;
    private readonly int _failureThreshold;
    private readonly TimeSpan _cooldownPeriod;

    private int _consecutiveFailures;
    private DateTime? _circuitOpenedAt;
    private readonly object _lock = new();

    /// <summary>
    /// True when the breaker is open and operations should be suppressed.
    /// </summary>
    public bool IsOpen
    {
        get
        {
            lock (_lock)
            {
                if (_circuitOpenedAt == null)
                {
                    return false;
                }

                if (DateTime.UtcNow - _circuitOpenedAt.Value >= _cooldownPeriod)
                {
                    return false;
                }

                return true;
            }
        }
    }

    /// <summary>
    /// Current consecutive-failure count.
    /// </summary>
    public int ConsecutiveFailures
    {
        get
        {
            lock (_lock)
            {
                return _consecutiveFailures;
            }
        }
    }

    /// <summary>
    /// Time remaining in the cooldown window, or null when the breaker is closed.
    /// </summary>
    public TimeSpan? CooldownRemaining
    {
        get
        {
            lock (_lock)
            {
                if (_circuitOpenedAt == null)
                {
                    return null;
                }

                var elapsed = DateTime.UtcNow - _circuitOpenedAt.Value;
                if (elapsed >= _cooldownPeriod)
                {
                    return null;
                }

                return _cooldownPeriod - elapsed;
            }
        }
    }

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    public CircuitBreaker(
        ILogger logger,
        string serviceName,
        int failureThreshold = 5,
        TimeSpan? cooldownPeriod = null)
    {
        _logger = logger;
        _serviceName = serviceName;
        _failureThreshold = failureThreshold;
        _cooldownPeriod = cooldownPeriod ?? TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// Resets the failure count after a successful operation.
    /// </summary>
    public void RecordSuccess()
    {
        lock (_lock)
        {
            if (_consecutiveFailures > 0 || _circuitOpenedAt != null)
            {
                _logger.LogInformation(
                    "Circuit breaker for {Service} reset after successful operation",
                    _serviceName);
            }

            _consecutiveFailures = 0;
            _circuitOpenedAt = null;
        }
    }

    /// <summary>
    /// Increments the failure count and opens the circuit if the threshold is reached.
    /// </summary>
    public void RecordFailure(string? errorMessage = null)
    {
        lock (_lock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures >= _failureThreshold)
            {
                _circuitOpenedAt = DateTime.UtcNow;
                _logger.LogWarning(
                    "Circuit breaker OPEN for {Service}: {Failures} consecutive failures. " +
                    "Sync operations will pause for {Cooldown} minutes. Last error: {Error}",
                    _serviceName,
                    _consecutiveFailures,
                    _cooldownPeriod.TotalMinutes,
                    errorMessage ?? "Unknown");
            }
            else if (_circuitOpenedAt == null)
            {
                _logger.LogDebug(
                    "Circuit breaker for {Service}: failure {Current}/{Threshold}. Error: {Error}",
                    _serviceName,
                    _consecutiveFailures,
                    _failureThreshold,
                    errorMessage ?? "Unknown");
            }
        }
    }

    /// <summary>
    /// Returns true if the operation should proceed; otherwise sets <paramref name="reason"/>.
    /// </summary>
    public bool AllowOperation(out string? reason)
    {
        lock (_lock)
        {
            // Direct field access avoids re-acquiring _lock via the properties.
            if (_circuitOpenedAt == null || DateTime.UtcNow - _circuitOpenedAt.Value >= _cooldownPeriod)
            {
                reason = null;
                return true;
            }

            var remaining = _cooldownPeriod - (DateTime.UtcNow - _circuitOpenedAt.Value);
            reason = $"Circuit breaker open for {_serviceName}: " +
                     $"{_consecutiveFailures} consecutive failures. " +
                     $"Retry in {remaining.TotalSeconds:F0} seconds.";
            return false;
        }
    }

    /// <summary>
    /// Forces the breaker back to a closed state.
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _consecutiveFailures = 0;
            _circuitOpenedAt = null;
            _logger.LogInformation("Circuit breaker for {Service} manually reset", _serviceName);
        }
    }
}
