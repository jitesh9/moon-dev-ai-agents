namespace GCTradingApp;

/// <summary>
/// Interface for all trading strategy engines.
/// </summary>
public interface IStrategyEngine
{
    /// <summary>
    /// Gets the name of the strategy.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Starts the strategy engine.
    /// </summary>
    void Start();

    /// <summary>
    /// Stops the strategy engine.
    /// </summary>
    void Stop();

    /// <summary>
    /// Processes a new bar of market data.
    /// </summary>
    /// <param name="bar">The market data bar.</param>
    void ProcessBar(BarData bar);

    /// <summary>
    /// Gets the current state of the strategy for persistence.
    /// </summary>
    StrategyState GetState();

    /// <summary>
    /// Event fired for logging messages from the strategy.
    /// </summary>
    event Action<string>? OnLog;

    /// <summary>
    /// Event fired when the strategy's state changes (e.g., position entry/exit).
    /// </summary>
    event Action<StrategyState>? OnStateChanged;

    /// <summary>
    /// Evaluates exit conditions for the strategy if in position.
    /// Returns null if not in position or if exit evaluation is not supported.
    /// </summary>
    /// <param name="bar">The current market data bar (optional, uses latest if not provided).</param>
    /// <returns>Exit conditions result, or null if not applicable.</returns>
    ExitConditionsResult? EvaluateExitConditions(BarData? bar = null);
}

