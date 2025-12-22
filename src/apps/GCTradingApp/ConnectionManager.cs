/*
 * Connection Manager for IBKR TWS
 * Handles auto-reconnection with exponential backoff
 */

namespace GCTradingApp;

/// <summary>
/// Connection state enumeration
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting
}

/// <summary>
/// Manages IBKR connection with automatic reconnection
/// </summary>
public class ConnectionManager : IDisposable
{
    private readonly object _lock = new();
    private IBKRClient? _client;
    private string _host = "127.0.0.1";
    private int _port = 7497;
    private int _clientId = 1;

    private ConnectionState _state = ConnectionState.Disconnected;
    private int _reconnectAttempt = 0;
    private bool _autoReconnectEnabled = true;
    private bool _wasConnected = false;
    private CancellationTokenSource? _reconnectCts;
    private Task? _reconnectTask;

    // Exponential backoff settings
    private readonly int[] _backoffDelaysMs = { 1000, 2000, 4000, 8000, 16000, 32000, 60000 };
    private const int MaxReconnectAttempts = 10;

    // Events
    public event Action<ConnectionState>? OnConnectionStateChanged;
    public event Action<string>? OnReconnectAttempt;
    public event Action<string>? OnLog;

    /// <summary>
    /// Current connection state
    /// </summary>
    public ConnectionState State
    {
        get { lock (_lock) return _state; }
        private set
        {
            lock (_lock)
            {
                if (_state != value)
                {
                    _state = value;
                    OnConnectionStateChanged?.Invoke(value);
                }
            }
        }
    }

    /// <summary>
    /// Whether auto-reconnection is enabled
    /// </summary>
    public bool AutoReconnectEnabled
    {
        get { lock (_lock) return _autoReconnectEnabled; }
        set { lock (_lock) _autoReconnectEnabled = value; }
    }

    /// <summary>
    /// Current reconnection attempt number (0 if not reconnecting)
    /// </summary>
    public int ReconnectAttempt
    {
        get { lock (_lock) return _reconnectAttempt; }
    }

    /// <summary>
    /// The underlying IBKR client
    /// </summary>
    public IBKRClient? Client => _client;

    /// <summary>
    /// Connect to TWS with the specified parameters
    /// </summary>
    public void Connect(string host, int port, int clientId)
    {
        lock (_lock)
        {
            _host = host;
            _port = port;
            _clientId = clientId;
            _reconnectAttempt = 0;
        }

        State = ConnectionState.Connecting;
        Log($"Connecting to TWS at {host}:{port}...");

        try
        {
            CreateAndConnectClient();
        }
        catch (Exception ex)
        {
            Log($"Connection failed: {ex.Message}");
            State = ConnectionState.Disconnected;
            throw;
        }
    }

    /// <summary>
    /// Disconnect from TWS and stop auto-reconnection
    /// </summary>
    public void Disconnect()
    {
        Log("Disconnecting...");

        // Stop any pending reconnection
        CancelReconnection();

        lock (_lock)
        {
            _wasConnected = false;
            _autoReconnectEnabled = false; // Disable auto-reconnect on manual disconnect
        }

        try
        {
            _client?.Disconnect();
        }
        catch (Exception ex)
        {
            Log($"Disconnect error: {ex.Message}");
        }

        State = ConnectionState.Disconnected;
        Log("Disconnected");
    }

    /// <summary>
    /// Enable auto-reconnection (call after manual disconnect if you want to reconnect)
    /// </summary>
    public void EnableAutoReconnect()
    {
        lock (_lock)
        {
            _autoReconnectEnabled = true;
        }
    }

    private void CreateAndConnectClient()
    {
        // Dispose old client if exists
        if (_client != null)
        {
            UnsubscribeFromClient();
            _client = null;
        }

        // Create new client
        _client = new IBKRClient();
        SubscribeToClient();

        string host;
        int port, clientId;
        lock (_lock)
        {
            host = _host;
            port = _port;
            clientId = _clientId;
        }

        _client.Connect(host, port, clientId);
    }

    private void SubscribeToClient()
    {
        if (_client == null) return;

        _client.OnConnected += HandleConnected;
        _client.OnDisconnected += HandleDisconnected;
        _client.OnError += HandleError;
    }

    private void UnsubscribeFromClient()
    {
        if (_client == null) return;

        _client.OnConnected -= HandleConnected;
        _client.OnDisconnected -= HandleDisconnected;
        _client.OnError -= HandleError;
    }

    private void HandleConnected()
    {
        lock (_lock)
        {
            _reconnectAttempt = 0;
            _wasConnected = true;
        }

        State = ConnectionState.Connected;
        Log("Connected to TWS successfully");
    }

    private void HandleDisconnected()
    {
        bool shouldReconnect;
        lock (_lock)
        {
            shouldReconnect = _wasConnected && _autoReconnectEnabled;
        }

        if (shouldReconnect)
        {
            State = ConnectionState.Reconnecting;
            StartReconnection();
        }
        else
        {
            State = ConnectionState.Disconnected;
        }
    }

    private void HandleError(int id, int errorCode, string errorMsg)
    {
        // Connection-related error codes
        // 502: Couldn't connect to TWS
        // 504: Not connected
        // 1100: Connectivity between IB and TWS has been lost
        // 1101: Connectivity restored, data lost
        // 1102: Connectivity restored, data maintained

        if (errorCode == 502 || errorCode == 504)
        {
            bool shouldReconnect;
            lock (_lock)
            {
                shouldReconnect = _autoReconnectEnabled;
            }

            if (shouldReconnect && State != ConnectionState.Reconnecting)
            {
                State = ConnectionState.Reconnecting;
                StartReconnection();
            }
        }
        else if (errorCode == 1100)
        {
            Log("TWS connectivity lost, waiting for reconnection...");
            // TWS will try to reconnect automatically, we just wait
        }
        else if (errorCode == 1101 || errorCode == 1102)
        {
            Log("TWS connectivity restored");
            State = ConnectionState.Connected;
            CancelReconnection();
        }
    }

    private void StartReconnection()
    {
        CancelReconnection();

        _reconnectCts = new CancellationTokenSource();
        var token = _reconnectCts.Token;

        _reconnectTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                int attempt;
                lock (_lock)
                {
                    _reconnectAttempt++;
                    attempt = _reconnectAttempt;

                    if (attempt > MaxReconnectAttempts)
                    {
                        Log($"Max reconnection attempts ({MaxReconnectAttempts}) exceeded. Giving up.");
                        _autoReconnectEnabled = false;
                        return;
                    }
                }

                int delayMs = _backoffDelaysMs[Math.Min(attempt - 1, _backoffDelaysMs.Length - 1)];
                var message = $"Reconnection attempt {attempt}/{MaxReconnectAttempts} in {delayMs / 1000.0:F1}s...";
                Log(message);
                OnReconnectAttempt?.Invoke(message);

                try
                {
                    await Task.Delay(delayMs, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested) return;

                try
                {
                    Log($"Attempting to reconnect (attempt {attempt})...");
                    CreateAndConnectClient();

                    // Wait a bit to see if connection succeeds
                    await Task.Delay(2000, token);

                    if (State == ConnectionState.Connected)
                    {
                        Log("Reconnection successful!");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Log($"Reconnection attempt {attempt} failed: {ex.Message}");
                }
            }
        }, token);
    }

    private void CancelReconnection()
    {
        try
        {
            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = null;
        }
        catch { }

        lock (_lock)
        {
            _reconnectAttempt = 0;
        }
    }

    private void Log(string message)
    {
        Logger.Info($"[ConnectionManager] {message}");
        OnLog?.Invoke(message);
    }

    public void Dispose()
    {
        CancelReconnection();

        if (_client != null)
        {
            UnsubscribeFromClient();
            try { _client.Disconnect(); } catch { }
            _client = null;
        }
    }
}
