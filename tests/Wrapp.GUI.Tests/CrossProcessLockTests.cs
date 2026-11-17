using Wrapp.Services;

namespace Wrapp.Tests;

/// <summary>
/// Workstream M2: cross-process RMW lock semantics. Named mutexes are
/// machine-wide kernel objects, so two threads acquiring the same name is an
/// exact stand-in for two processes doing so.
/// </summary>
public sealed class CrossProcessLockTests
{
    [Fact]
    public void Run_ExecutesBody()
    {
        var ran = false;
        CrossProcessLock.Run($"Test_{Guid.NewGuid():N}", () => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void Run_IsReentrantOnTheSameThread()
    {
        var name = $"Test_{Guid.NewGuid():N}";
        var inner = false;
        CrossProcessLock.Run(name, () => CrossProcessLock.Run(name, () => inner = true));
        Assert.True(inner);
    }

    [Theory]
    [InlineData("app.pid1234.log", true)]
    [InlineData("app.pid1234.3.log", true)]
    [InlineData("app.log", false)]
    [InlineData("app.1.log", false)]     // primary rotation generation, never cleaned
    [InlineData("app.pid.log", false)]
    public void AppLogger_IsInstanceLogName_DistinguishesInstanceLogs(string name, bool expected)
    {
        Assert.Equal(expected, AppLogger.IsInstanceLogName(name));
    }

    [Fact]
    public async Task Run_SerializesCyclesOnTheSameName()
    {
        var name = $"Test_{Guid.NewGuid():N}";
        var events = new List<string>();
        var firstInside = new SemaphoreSlim(0);
        var releaseFirst = new SemaphoreSlim(0);

        var first = Task.Run(() => CrossProcessLock.Run(name, () =>
        {
            lock (events) events.Add("first-start");
            firstInside.Release();
            releaseFirst.Wait(TimeSpan.FromSeconds(10));
            lock (events) events.Add("first-end");
        }));

        Assert.True(await firstInside.WaitAsync(TimeSpan.FromSeconds(10)));

        var second = Task.Run(() => CrossProcessLock.Run(name, () =>
        {
            lock (events) events.Add("second");
        }));

        // Second must not enter while first holds the lock
        await Task.Delay(300);
        lock (events) Assert.DoesNotContain("second", events);

        releaseFirst.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(new[] { "first-start", "first-end", "second" }, events);
    }

    [Fact]
    public async Task Run_DifferentNames_DoNotBlockEachOther()
    {
        var gate = new SemaphoreSlim(0);
        var holderInside = new SemaphoreSlim(0);

        var holder = Task.Run(() => CrossProcessLock.Run($"Test_{Guid.NewGuid():N}", () =>
        {
            holderInside.Release();
            gate.Wait(TimeSpan.FromSeconds(10));
        }));

        Assert.True(await holderInside.WaitAsync(TimeSpan.FromSeconds(10)));

        var ran = false;
        CrossProcessLock.Run($"Test_{Guid.NewGuid():N}", () => ran = true);
        Assert.True(ran);

        gate.Release();
        await holder;
    }

    [Fact]
    public async Task Run_TimeoutProceedsWithoutLock()
    {
        var name = $"Test_{Guid.NewGuid():N}";
        var holderInside = new SemaphoreSlim(0);
        var gate = new SemaphoreSlim(0);

        var holder = Task.Run(() => CrossProcessLock.Run(name, () =>
        {
            holderInside.Release();
            gate.Wait(TimeSpan.FromSeconds(10));
        }));

        Assert.True(await holderInside.WaitAsync(TimeSpan.FromSeconds(10)));

        // S-4 semantics: progress over hang -- the body still runs on timeout
        var ran = false;
        CrossProcessLock.Run(name, () => ran = true, timeoutMs: 150);
        Assert.True(ran);

        gate.Release();
        await holder;
    }
}
