/*
 * State Manager
 * Centralized state persistence with atomic saves and async I/O
 */

using System.Collections.Concurrent;
using Newtonsoft.Json;

namespace GCTradingApp;

/// <summary>
/// Centralized state manager for atomic persistence
/// Handles state updates from multiple sources with async I/O
/// </summary>
public class StateManager : IDisposable
{
    private readonly string _stateFilePath;
    private readonly object _stateLock = new();
    private AppState _state;
    private readonly ConcurrentQueue<Action<AppState>> _pendingUpdates = new();
    private readonly SemaphoreSlim _saveSemaphore = new(1, 1);
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private readonly System.Threading.Timer _autoSaveTimer;
    private bool _disposed = false;

    // Events
    public event Action<string>? OnLog;
    public event Action<Exception>? OnError;

    public StateManager(string stateFilePath, AppState initialState)
    {
        _stateFilePath = stateFilePath ?? throw new ArgumentNullException(nameof(stateFilePath));
        _state = initialState ?? throw new ArgumentNullException(nameof(initialState));

        // Auto-save every 30 seconds
        _autoSaveTimer = new System.Threading.Timer(AutoSaveCallback, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));

        Log("StateManager initialized");
    }

    /// <summary>
    /// Get current state snapshot (thread-safe)
    /// </summary>
    public AppState GetState()
    {
        lock (_stateLock)
        {
            // Return a deep copy to prevent external modification
            var json = JsonConvert.SerializeObject(_state);
            return JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
        }
    }

    /// <summary>
    /// Update state atomically
    /// </summary>
    public void UpdateState(Action<AppState> updateAction)
    {
        if (_disposed) return;

        lock (_stateLock)
        {
            updateAction(_state);
        }

        // Queue for async save
        _pendingUpdates.Enqueue(updateAction);
    }

    /// <summary>
    /// Save state immediately (synchronous, for shutdown)
    /// </summary>
    public void SaveNow()
    {
        if (_disposed) return;

        try
        {
            _saveSemaphore.Wait();

            string json;
            lock (_stateLock)
            {
                json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            }

            // Atomic write: write to temp file, then rename
            var tempPath = _stateFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _stateFilePath, overwrite: true);

            Log("State saved successfully");
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save state", ex);
            OnError?.Invoke(ex);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    /// <summary>
    /// Save state asynchronously
    /// </summary>
    public async Task SaveAsync()
    {
        if (_disposed) return;

        try
        {
            await _saveSemaphore.WaitAsync(_cancellationTokenSource.Token);

            string json;
            lock (_stateLock)
            {
                json = JsonConvert.SerializeObject(_state, Formatting.Indented);
            }

            // Atomic write: write to temp file, then rename
            var tempPath = _stateFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, json, _cancellationTokenSource.Token);
            File.Move(tempPath, _stateFilePath, overwrite: true);

            Log("State saved asynchronously");
        }
        catch (OperationCanceledException)
        {
            // Expected during shutdown
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to save state asynchronously", ex);
            OnError?.Invoke(ex);
        }
        finally
        {
            _saveSemaphore.Release();
        }
    }

    /// <summary>
    /// Load state from file
    /// </summary>
    public static AppState LoadState(string stateFilePath)
    {
        try
        {
            if (File.Exists(stateFilePath))
            {
                var json = File.ReadAllText(stateFilePath);
                return JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to load state: {ex.Message}", ex);
        }
        return new AppState();
    }

    /// <summary>
    /// Update strategy state
    /// </summary>
    public void UpdateStrategyState(string strategyName, StrategyState state)
    {
        UpdateState(s =>
        {
            switch (strategyName)
            {
                case "Aggressive":
                    s.AggressiveState = state;
                    break;
                case "Conservative":
                    s.ConservativeState = state;
                    break;
                default:
                    if (state is MTFStrategyState mtfState)
                    {
                        s.MTFState = mtfState;
                    }
                    break;
            }
        });
    }

    /// <summary>
    /// Update risk state
    /// </summary>
    public void UpdateRiskState(RiskState state)
    {
        UpdateState(s => s.RiskState = state);
    }

    /// <summary>
    /// Update performance state
    /// </summary>
    public void UpdatePerformanceState(List<CompletedTrade> trades, double currentEquity)
    {
        UpdateState(s =>
        {
            s.CompletedTrades = trades;
            s.CurrentEquity = currentEquity;
        });
    }

    private void AutoSaveCallback(object? state)
    {
        if (_disposed) return;

        // Fire and forget async save
        _ = Task.Run(async () =>
        {
            try
            {
                await SaveAsync();
            }
            catch (Exception ex)
            {
                Logger.Error("Auto-save failed", ex);
            }
        });
    }

    private void Log(string message)
    {
        Logger.Info($"[StateManager] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _autoSaveTimer?.Dispose();
        _cancellationTokenSource.Cancel();
        _cancellationTokenSource.Dispose();

        // Final save on disposal
        SaveNow();

        _saveSemaphore.Dispose();
    }
}

