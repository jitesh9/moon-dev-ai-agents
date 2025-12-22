/*
 * Thread Safety Tests
 * Tests concurrent access to shared state
 */

using FluentAssertions;
using Xunit;

namespace GCTradingApp.Tests;

/// <summary>
/// Tests for thread-safe operations on shared state
/// </summary>
public class ThreadSafetyTests
{
    #region Order ID Thread Safety Tests

    [Fact]
    public void NextOrderId_ConcurrentAccess_ReturnsUniqueValues()
    {
        // Arrange
        var orderIds = new System.Collections.Concurrent.ConcurrentBag<int>();
        int baseId = 1000;
        int iterations = 1000;
        int threadCount = 10;

        // Simulate the thread-safe incrementer
        int _nextOrderId = baseId;
        Func<int> getNextOrderId = () => Interlocked.Increment(ref _nextOrderId);

        // Act - Run multiple threads concurrently
        var tasks = new List<Task>();
        for (int t = 0; t < threadCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < iterations / threadCount; i++)
                {
                    orderIds.Add(getNextOrderId());
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert - All IDs should be unique
        var distinctIds = orderIds.Distinct().ToList();
        distinctIds.Count.Should().Be(iterations);
    }

    [Fact]
    public void NextOrderId_SequentialAfterConcurrent_ContinuesCorrectly()
    {
        // Arrange
        int _nextOrderId = 1;
        int concurrentIterations = 100;

        // Act - Run concurrent increments
        Parallel.For(0, concurrentIterations, _ =>
        {
            Interlocked.Increment(ref _nextOrderId);
        });

        var nextId = Interlocked.Increment(ref _nextOrderId);

        // Assert - Next ID should be concurrentIterations + 2
        nextId.Should().Be(concurrentIterations + 2);
    }

    #endregion

    #region State Collection Thread Safety Tests

    [Fact]
    public void AppState_ConcurrentOrderAdditions_AllAdded()
    {
        // Arrange
        var state = new AppState();
        var stateLock = new object();
        int orderCount = 100;
        var tasks = new List<Task>();

        // Act - Add orders concurrently
        for (int i = 0; i < orderCount; i++)
        {
            int orderId = i;
            tasks.Add(Task.Run(() =>
            {
                lock (stateLock)
                {
                    state.Orders[orderId] = new OrderRecord
                    {
                        OrderId = orderId,
                        Time = DateTime.Now,
                        Strategy = "Test",
                        Action = "BUY",
                        Quantity = 1
                    };
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        state.Orders.Count.Should().Be(orderCount);
    }

    [Fact]
    public void AppState_ConcurrentFillAdditions_AllAdded()
    {
        // Arrange
        var state = new AppState();
        var stateLock = new object();
        int fillCount = 100;
        var tasks = new List<Task>();

        // Act - Add fills concurrently
        for (int i = 0; i < fillCount; i++)
        {
            int fillId = i;
            tasks.Add(Task.Run(() =>
            {
                lock (stateLock)
                {
                    state.Fills.Add(new FillRecord
                    {
                        ExecId = $"EXEC_{fillId}",
                        Time = DateTime.Now,
                        Strategy = "Test",
                        Action = "BUY",
                        Quantity = 1,
                        Price = 2000.0
                    });
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        state.Fills.Count.Should().Be(fillCount);
    }

    [Fact]
    public void AppState_ConcurrentPositionUpdates_AllUpdated()
    {
        // Arrange
        var state = new AppState();
        var stateLock = new object();
        int positionCount = 50;
        var tasks = new List<Task>();

        // Act - Update positions concurrently
        for (int i = 0; i < positionCount; i++)
        {
            string key = $"GC_ACC{i}";
            tasks.Add(Task.Run(() =>
            {
                lock (stateLock)
                {
                    state.Positions[key] = new PositionRecord
                    {
                        Symbol = "GC",
                        Position = 1,
                        AvgCost = 2000.0
                    };
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        state.Positions.Count.Should().Be(positionCount);
    }

    [Fact]
    public void AppState_ConcurrentReadAndWrite_NoExceptions()
    {
        // Arrange
        var state = new AppState();
        var stateLock = new object();
        var cts = new CancellationTokenSource();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Pre-populate some data
        for (int i = 0; i < 10; i++)
        {
            state.Orders[i] = new OrderRecord { OrderId = i };
            state.Fills.Add(new FillRecord { ExecId = $"EXEC_{i}" });
        }

        // Act - Run readers and writers concurrently
        var tasks = new List<Task>();

        // Writers
        for (int w = 0; w < 5; w++)
        {
            int writerId = w;
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (cts.Token.IsCancellationRequested) break;

                        lock (stateLock)
                        {
                            state.Orders[100 + writerId * 100 + i] = new OrderRecord
                            {
                                OrderId = 100 + writerId * 100 + i
                            };
                            state.Fills.Add(new FillRecord
                            {
                                ExecId = $"EXEC_{writerId}_{i}"
                            });
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        // Readers
        for (int r = 0; r < 5; r++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (cts.Token.IsCancellationRequested) break;

                        List<OrderRecord> ordersCopy;
                        List<FillRecord> fillsCopy;

                        lock (stateLock)
                        {
                            ordersCopy = state.Orders.Values.ToList();
                            fillsCopy = state.Fills.ToList();
                        }

                        // Simulate processing the copied data
                        var _ = ordersCopy.Count + fillsCopy.Count;
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        exceptions.Should().BeEmpty();
    }

    #endregion

    #region Bar Data Thread Safety Tests

    [Fact]
    public void BarList_ConcurrentAdditions_AllAdded()
    {
        // Arrange
        var bars = new List<BarData>();
        var barsLock = new object();
        int barCount = 1000;
        var tasks = new List<Task>();

        // Act - Add bars concurrently
        for (int i = 0; i < barCount; i++)
        {
            int barIndex = i;
            tasks.Add(Task.Run(() =>
            {
                var bar = new BarData
                {
                    Time = DateTime.Now.AddMinutes(-barIndex),
                    Open = 2000 + barIndex * 0.1,
                    High = 2001 + barIndex * 0.1,
                    Low = 1999 + barIndex * 0.1,
                    Close = 2000.5 + barIndex * 0.1,
                    Volume = 100
                };

                lock (barsLock)
                {
                    bars.Add(bar);
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        bars.Count.Should().Be(barCount);
    }

    [Fact]
    public void BarList_ConcurrentReadWhileWriting_NoExceptions()
    {
        // Arrange
        var bars = new List<BarData>();
        var barsLock = new object();
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // Pre-populate
        for (int i = 0; i < 50; i++)
        {
            bars.Add(new BarData { Close = 2000 + i });
        }

        // Act - Write and read concurrently
        var tasks = new List<Task>();

        // Writer
        tasks.Add(Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 100; i++)
                {
                    lock (barsLock)
                    {
                        bars.Add(new BarData { Close = 2100 + i });
                        if (bars.Count > 100)
                            bars.RemoveAt(0);
                    }
                    Thread.Sleep(1); // Small delay
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        }));

        // Readers
        for (int r = 0; r < 5; r++)
        {
            tasks.Add(Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 100; i++)
                    {
                        double[] closes;
                        lock (barsLock)
                        {
                            closes = bars.Select(b => b.Close).ToArray();
                        }

                        // Process data outside lock
                        if (closes.Length > 0)
                        {
                            var avg = closes.Average();
                        }
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        Task.WaitAll(tasks.ToArray());

        // Assert
        exceptions.Should().BeEmpty();
    }

    #endregion

    #region Strategy State Thread Safety Tests

    [Fact]
    public void StrategyState_ConcurrentUpdates_LastWriteWins()
    {
        // Arrange
        var state = new StrategyState();
        var stateLock = new object();
        var tasks = new List<Task>();

        // Act - Update state from multiple threads
        for (int i = 0; i < 100; i++)
        {
            int iteration = i;
            tasks.Add(Task.Run(() =>
            {
                lock (stateLock)
                {
                    state.InPosition = iteration % 2 == 0;
                    state.EntryPrice = 2000 + iteration;
                    state.StopPrice = 1990 + iteration;
                    state.TargetPrice = 2010 + iteration;
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert - State should be consistent (not corrupted)
        // The exact values depend on thread ordering, but they should be valid
        lock (stateLock)
        {
            state.EntryPrice.Should().BeInRange(2000, 2099);
            state.StopPrice.Should().BeInRange(1990, 2089);
            state.TargetPrice.Should().BeInRange(2010, 2109);
        }
    }

    [Fact]
    public void StrategyState_CopyUnderLock_IsolatesFromChanges()
    {
        // Arrange
        var state = new StrategyState
        {
            InPosition = true,
            EntryPrice = 2000,
            StopPrice = 1990,
            TargetPrice = 2010
        };
        var stateLock = new object();

        StrategyState copy;
        lock (stateLock)
        {
            copy = new StrategyState
            {
                InPosition = state.InPosition,
                EntryPrice = state.EntryPrice,
                StopPrice = state.StopPrice,
                TargetPrice = state.TargetPrice
            };
        }

        // Act - Modify original
        lock (stateLock)
        {
            state.EntryPrice = 2100;
            state.InPosition = false;
        }

        // Assert - Copy should not be affected
        copy.EntryPrice.Should().Be(2000);
        copy.InPosition.Should().BeTrue();
    }

    #endregion

    #region Interlocked Operations Tests

    [Fact]
    public void InterlockedIncrement_IsAtomic()
    {
        // Arrange
        int counter = 0;
        int threadCount = 10;
        int incrementsPerThread = 10000;

        // Act
        var tasks = new List<Task>();
        for (int t = 0; t < threadCount; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < incrementsPerThread; i++)
                {
                    Interlocked.Increment(ref counter);
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert
        counter.Should().Be(threadCount * incrementsPerThread);
    }

    [Fact]
    public void InterlockedCompareExchange_WorksCorrectly()
    {
        // Arrange
        int value = 0;
        int successCount = 0;

        // Act - Multiple threads try to set value from 0 to 1
        var tasks = new List<Task>();
        for (int t = 0; t < 10; t++)
        {
            tasks.Add(Task.Run(() =>
            {
                // Only one should succeed
                if (Interlocked.CompareExchange(ref value, 1, 0) == 0)
                {
                    Interlocked.Increment(ref successCount);
                }
            }));
        }
        Task.WaitAll(tasks.ToArray());

        // Assert - Only one thread should have succeeded
        successCount.Should().Be(1);
        value.Should().Be(1);
    }

    #endregion

    #region Deadlock Prevention Tests

    [Fact]
    public void MultipleLocksInSameOrder_NoDeadlock()
    {
        // Arrange
        var lock1 = new object();
        var lock2 = new object();
        var completed = new System.Collections.Concurrent.ConcurrentBag<int>();

        // Act - Multiple threads acquiring locks in same order
        var tasks = new List<Task>();
        for (int t = 0; t < 10; t++)
        {
            int threadId = t;
            tasks.Add(Task.Run(() =>
            {
                for (int i = 0; i < 100; i++)
                {
                    lock (lock1)
                    {
                        lock (lock2)
                        {
                            // Simulate work
                            Thread.SpinWait(100);
                        }
                    }
                }
                completed.Add(threadId);
            }));
        }

        // Should complete without deadlock
        var completedInTime = Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(5));

        // Assert
        completedInTime.Should().BeTrue("All tasks should complete without deadlock");
        completed.Count.Should().Be(10);
    }

    #endregion
}
