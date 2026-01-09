/*
 * Circuit Breaker Pattern Implementation
 * Prevents cascading failures by automatically disabling components after repeated failures
 */

namespace GCTradingApp;

/// <summary>
/// Circuit breaker state
/// </summary>
public enum CircuitState
{
    /// <summary>
    /// Circuit is closed - operations proceed normally
    /// </summary>
    Closed,

    /// <summary>
    /// Circuit is open - operations are blocked
    /// </summary>
    Open,

    /// <summary>
    /// Circuit is half-open - testing if service has recovered
    /// </summary>
    HalfOpen
}

/// <summary>
/// Circuit breaker configuration
/// </summary>
public class CircuitBreakerConfig
{
    /// <summary>
    /// Number of failures before opening the circuit
    /// </summary>
    public int FailureThreshold { get; set; } = 5;

    /// <summary>
    /// Time window in seconds for counting failures
    /// </summary>
    public int TimeWindowSeconds { get; set; } = 60;

    /// <summary>
    /// Timeout in seconds before attempting to close circuit (half-open state)
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Number of successful operations required in half-open state to close circuit
    /// </summary>
    public int SuccessThreshold { get; set; } = 2;
}

/// <summary>
/// Circuit breaker implementation to prevent cascading failures
/// </summary>
public class CircuitBreaker
{
    public string Name { get; }
    private readonly CircuitBreakerConfig _config;
    private CircuitState _state;
    private DateTime _lastFailureTime;
    private int _failureCount;
    private int _successCount;
    private readonly object _lock = new();

    public event Action<string, CircuitState>? OnStateChanged;
    public event Action<string>? OnLog;

    public CircuitBreaker(string name, CircuitBreakerConfig config)
    {
        Name = name;
        _config = config;
        _state = CircuitState.Closed;
        _failureCount = 0;
        _successCount = 0;
    }

    /// <summary>
    /// Execute an operation with circuit breaker protection
    /// </summary>
    /// <param name="operation">The operation to execute</param>
    /// <returns>True if operation was executed, false if blocked</returns>
    public bool Execute(Action operation)
    {
        lock (_lock)
        {
            if (_state == CircuitState.Open)
            {
                if (DateTime.UtcNow - _lastFailureTime > TimeSpan.FromSeconds(_config.TimeoutSeconds))
                {
                    // Attempt to transition to HalfOpen
                    _state = CircuitState.HalfOpen;
                    _successCount = 0; // Reset success count for half-open state
                    OnStateChanged?.Invoke(Name, _state);
                    Log($"Circuit for {Name} transitioned to HalfOpen. Attempting operation.");
                }
                else
                {
                    Log($"Circuit for {Name} is Open. Operation blocked.");
                    return false; // Still in open state, block operation
                }
            }

            try
            {
                operation();
                RecordSuccess();
                return true;
            }
            catch (Exception ex)
            {
                RecordFailure(ex);
                return false;
            }
        }
    }

    private void RecordFailure(Exception ex)
    {
        _failureCount++;
        _lastFailureTime = DateTime.UtcNow;
        Log($"Operation for {Name} failed. Failure count: {_failureCount}. Error: {ex.Message}");

        if (_state == CircuitState.HalfOpen)
        {
            // If failed in HalfOpen, immediately go back to Open
            _state = CircuitState.Open;
            OnStateChanged?.Invoke(Name, _state);
            Log($"Circuit for {Name} transitioned to Open from HalfOpen due to failure.");
        }
        else if (_state == CircuitState.Closed && _failureCount >= _config.FailureThreshold)
        {
            _state = CircuitState.Open;
            OnStateChanged?.Invoke(Name, _state);
            Log($"Circuit for {Name} transitioned to Open due to {_failureCount} failures.");
        }
    }

    private void RecordSuccess()
    {
        if (_state == CircuitState.HalfOpen)
        {
            _successCount++;
            Log($"Operation for {Name} succeeded in HalfOpen state. Success count: {_successCount}.");
            if (_successCount >= _config.SuccessThreshold)
            {
                _state = CircuitState.Closed;
                _failureCount = 0;
                _successCount = 0;
                OnStateChanged?.Invoke(Name, _state);
                Log($"Circuit for {Name} transitioned to Closed after {_successCount} successes in HalfOpen.");
            }
        }
        else if (_state == CircuitState.Closed)
        {
            _failureCount = 0; // Reset failure count on success in closed state
        }
    }

    /// <summary>
    /// Manually reset the circuit breaker to Closed state
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _state = CircuitState.Closed;
            _failureCount = 0;
            _successCount = 0;
            OnStateChanged?.Invoke(Name, _state);
            Log($"Circuit for {Name} manually reset to Closed.");
        }
    }

    public CircuitState State => _state;

    private void Log(string message)
    {
        Logger.Info($"[CircuitBreaker:{Name}] {message}");
        OnLog?.Invoke(message);
    }
}

