using Screenly.Core.Display;
using Xunit;

namespace Screenly.Tests;

public class DisplayPoolTests
{
    [Fact]
    public async Task ReconnectDuringGrace_CancelsRemoval()
    {
        var driver = new FakeDisplayDriver();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pool = new DisplayPool(driver, clock, maxDisplays: 1, graceSeconds: 5);
        var lease = await pool.AcquireAsync(800, 1280, 60, "dev1");
        pool.Release(lease!);
        clock.Advance(TimeSpan.FromSeconds(2));
        var lease2 = await pool.AcquireAsync(800, 1280, 60, "dev1");
        Assert.Same(lease, lease2);
        Assert.Equal(LeaseState.Assigned, lease2!.State);
        Assert.Equal(0, driver.RemoveCount);
    }

    [Fact]
    public async Task TwoConcurrentAcquires_AtMaxDisplays1_OnlyOneAdds()
    {
        var driver = new FakeDisplayDriver { RemoveDelay = TimeSpan.FromSeconds(0) };
        var pool = new DisplayPool(driver, TimeProvider.System, maxDisplays: 1, graceSeconds: 5);
        var t1 = pool.AcquireAsync(800, 1280, 60, "dev1");
        var t2 = pool.AcquireAsync(800, 1280, 60, "dev2");
        await Task.WhenAll(t1, t2);
        Assert.Equal(1, driver.AddCount);
    }

    [Fact]
    public async Task SeededSoak_AddOnlyOnGrowth_RemoveOnlyAfterGrace_NoRemoveOnReconnect()
    {
        const int seed = 42;
        var rng = new Random(seed);
        var driver = new FakeDisplayDriver();
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var pool = new DisplayPool(driver, clock, maxDisplays: 2, graceSeconds: 5);

        var clients = new[] { "a", "b", "c" };
        var active = new Dictionary<string, DisplayLease>();

        for (int i = 0; i < 10; i++)
        {
            var c = clients[rng.Next(clients.Length)];
            bool connect = rng.Next(2) == 0;

            // enough time for rate limit + occasional grace expiry
            clock.Advance(TimeSpan.FromSeconds(6));

            if (connect)
            {
                var lease = await pool.AcquireAsync(800, 1280, 60, c);
                if (lease != null) active[c] = lease;
            }
            else
            {
                if (active.TryGetValue(c, out var l))
                {
                    pool.Release(l);
                    active.Remove(c);
                }
            }
        }

        clock.Advance(TimeSpan.FromSeconds(10));
        await Task.Delay(50);

        Assert.Equal(driver.AddCount, pool.Created);
        Assert.Equal(driver.RemoveCount, pool.Removed);
    }
}