using Screenly.Core.Config;
using Screenly.Core.Streaming;
using Xunit;

namespace Screenly.Tests;

public class ConfigStoreTests
{
    [Fact]
    public void DefaultConfig_HasExpectedValues()
    {
        var c = new AppConfig();
        Assert.Equal(7543, c.Port);
        Assert.Equal(1920, c.VirtualDisplay.Width);
    }
}

public class ProtocolFramingTests
{
    [Fact]
    public async Task RoundTrip_Message()
    {
        using var ms = new MemoryStream();
        var payload = new byte[] { 1, 2, 3 };
        await Protocol.WriteMessageAsync(ms, MessageType.Ping, payload);
        ms.Position = 0;
        var (type, got) = await Protocol.ReadMessageAsync(ms);
        Assert.Equal(MessageType.Ping, type);
        Assert.Equal(payload, got);
    }
}
