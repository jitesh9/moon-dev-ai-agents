/*
 * Circuit Breaker Pattern Implementation
 * Automatically disables strategies after repeated failures
 */

namespace GCTradingApp;

/// <summary>
/// Circuit breaker state
/// </summary>
public enum CircuitState
{
    Closed,      // Normal operation
    Open,        // Circuit is open, blocking operations
    HalfOpen     // Testing if service has recovered
}

/// <summary>
/// Circuit breaker configuration
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>
    /// Number of failures before opening circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Time window in seconds for counting failures
    /// </summary>
    public int TimeWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Time in seconds to wait before attempting half-open
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of successful calls in half-open state to close circuit
    /// </summary>
    public int SuccessThreshold { get; set; } = 2;
}

/// <summary>
/// Circuit breaker to prevent cascading failures
/// Tracks failures and automatically disables operations after threshold
/// </summary>
public class CircuitBreaker
{
    private readonly CircuitBreakerConfig _config;
    private readonly object _lock = new();
    private readonly string _name;

    private CircuitState _state = CircuitState.Closed;
    private int _failureCount = 0;
    private DateTime _lastFailureTime = DateTime.MinValue;
    private DateTime _openedAt = DateTime.MinValue;
    private int _halfOpenSuccessCount = 0;

    // Events
    public event Action<string, CircuitState>? OnStateChanged;
    public event Action<string>? OnLog;

    public CircuitState State
    {
        get { lock (_lock) return _state; }
    }

    public string Name => _name;

    public CircuitBreaker(string name, CircuitBreakerConfig? config = null)
    {
        _name = name ?? throw new ArgumentNullException(nameof(name));
        _config = config ?? new CircuitBreakerConfig();
    }

    /// <summary>
    /// Execute an operation with circuit breaker protection
    /// </summary>
    public bool Execute(Action operation)
    {
        lock (_lock)
        {
            // Check if circuit is open
            if (_state == CircuitState.Open)
            {
                // Check if timeout has elapsed
                if (DateTime.Now - _openedAt >= TimeSpan.FromSeconds(_config.TimeoutSeconds))
                {
                    // Transition to half-open
                    _state = CircuitState.HalfOpen;
                    _halfOpenSuccessCount = 0;
                    Log($"Circuit breaker transitioning to HalfOpen for {_name}");
                    OnStateChanged?.Invoke(_name, _state);
                }
                else
                {
                    // Still in timeout period
                    Log($"Circuit breaker is OPEN for {_name}, operation blocked");
                    return false;
                }
            }

            // Try to execute
            try
            {
                operation();
                OnSuccess();
                return true;
            }
            catch (Exception ex)
            {
                OnFailure(ex);
                throw; // Re-throw exception
            }
        }
    }

    /// <summary>
    /// Execute an async operation with circuit breaker protection
    /// </summary>
    public async Task<bool> ExecuteAsync(Func<Task> operation)
    {
        lock (_lock)
        {
            // Check if circuit is open
            if (_state == CircuitState.Open)
            {
                // Check if timeout has elapsed
                if (DateTime.Now - _openedAt >= TimeSpan.FromSeconds(_config.TimeoutSeconds))
                {
                    // Transition to half-open
                    _state = CircuitState.HalfOpen;
                    _halfOpenSuccessCount = 0;
                    Log($"Circuit breaker transitioning to HalfOpen for {_name}");
                    OnStateChanged?.Invoke(_name, _state);
                }
                else
                {
                    // Still in timeout period
                    Log($"Circuit breaker is OPEN for {_name}, operation blocked");
                    return false;
                }
            }
        }

        // Try to execute (outside lock for async)
        try
        {
            await operation();
            lock (_lock)
            {
                OnSuccess();
            }
            return true;
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                OnFailure(ex);
            }
            throw; // Re-throw exception
        }
    }

    private void OnSuccess()
    {
        if (_state == CircuitState.HalfOpen)
        {
            _halfOpenSuccessCount++;
            if (_halfOpenSuccessCount >= _config.SuccessThreshold)
            {
                // Close the circuit
                _state = CircuitState.Closed;
                _failureCount = 0;
                _halfOpenSuccessCount = 0;
                Log($"Circuit breaker CLOSED for {_name} after successful recovery");
                OnStateChanged?.Invoke(_name, _state);
            }
        }
        else if (_state == CircuitState.Closed)
        {
            // Reset failure count on success (sliding window)
            var now = DateTime.Now;
            if (now - _lastFailureTime >= TimeSpan.FromSeconds(_config.TimeWindowSeconds))
            {
                _failureCount = 0;
            }
        }
    }

    private void OnFailure(Exception ex)
    {
        var now = DateTime.Now;

        // Reset failure count if outside time window
        if (now - _lastFailureTime >= TimeSpan.FromSeconds(_config.TimeWindowSeconds))
        {
            _failureCount = 0;
        }

        _failureCount++;
        _lastFailureTime = now;

        Log($"Circuit breaker failure #{_failureCount} for {_name}: {ex.Message}");

        if (_state == CircuitState.HalfOpen)
        {
            // Immediately open on failure in half-open state
            _state = CircuitState.Open;
            _openedAt = now;
            Log($"Circuit breaker OPENED for {_name} (failed in half-open state)");
            OnStateChanged?.Invoke(_name, _state);
        }
        else if (_state == CircuitState.Closed && _failureCount >= _config.FailureThreshold)
        {
            // Open the circuit
            _state = CircuitState.Open;
            _openedAt = now;
            Log($"Circuit breaker OPENED for {_name} after {_failureCount} failures");
            OnStateChanged?.Invoke(_name, _state);
        }
    }

    /// <summary>
    /// Manually reset the circuit breaker
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _halfOpenSuccessCount = 0;
            _lastFailureTime = DateTime.MinValue;
            _openedAt = DateTime.MinValue;
            Log($"Circuit breaker manually RESET for {_name}");
            OnStateChanged?.Invoke(_name, _state);
        }
    }

    /// <summary>
    /// Manually open the circuit breaker
    /// </summary>
    public void Open()
    {
        lock (_lock)
        {
            if (_state != CircuitState.Open)
            {
                _state = CircuitState.Open;
                _openedAt = DateTime.Now;
                Log($"Circuit breaker manually OPENED for {_name}");
                OnStateChanged?.Invoke(_name, _state);
            }
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[CircuitBreaker:{_name}] {message}");
        OnLog?.Invoke(message);
    }
}

