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
        pool.Release(lease);
        clock.Advance(TimeSpan.FromSeconds(2));
        var lease2 = await pool.AcquireAsync(800, 1280, 60, "dev1");
        Assert.Same(lease, lease2);
        Assert.Equal(LeaseState.Assigned, lease2.State);
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
}
