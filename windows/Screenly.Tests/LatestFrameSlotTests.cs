using Screenly.Core.Streaming;
using Xunit;

namespace Screenly.Tests;

public class LatestFrameSlotTests
{
    [Fact]
    public async Task Publish_LatestWins_OnlyNewestFrameIsSent()
    {
        var slot = new LatestFrameSlot();

        slot.Publish(new byte[] { 1 });
        slot.Publish(new byte[] { 2 });

        Assert.True(slot.HasPending);
        var taken = await slot.TakeAsync();
        Assert.Equal(new byte[] { 2 }, taken);
        Assert.False(slot.HasPending);
    }

    [Fact]
    public async Task Publish_ReportsReplacement_OnlyWhenAFrameWasPending()
    {
        var slot = new LatestFrameSlot();

        Assert.False(slot.Publish(new byte[] { 1 })); // slot was empty
        Assert.True(slot.Publish(new byte[] { 2 }));  // replaced the unsent frame

        await slot.TakeAsync();
        Assert.False(slot.Publish(new byte[] { 3 })); // empty again after take
    }

    [Fact]
    public async Task TakeAsync_WaitsForPublish()
    {
        var slot = new LatestFrameSlot();

        var take = slot.TakeAsync();
        Assert.False(take.IsCompleted);

        slot.Publish(new byte[] { 7 });
        var frame = await take.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new byte[] { 7 }, frame);
    }

    [Fact]
    public async Task TakeAsync_HonoursCancellation()
    {
        var slot = new LatestFrameSlot();
        using var cts = new CancellationTokenSource();

        var take = slot.TakeAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => take);
    }
}
