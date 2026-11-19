using Screenly.Core.Config;
using Xunit;

namespace Screenly.Tests;

// Covers the engine's codec poll: ConfigStore.Save replaces config.json atomically
// (tmp + File.Replace), so the poller must see the new codec on the very next read.
public class ConfigCodecPollTests
{
    [Fact]
    public void ReadCodec_SeesCodecAfterAtomicSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "screenly-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var jpeg = new AppConfig();
            ConfigStore.Save(jpeg, path); // first save: File.Move
            Assert.Equal("jpeg", ConfigStore.ReadCodec(path));

            var h264 = jpeg with { Encoding = jpeg.Encoding with { Codec = "h264" } };
            ConfigStore.Save(h264, path); // subsequent save: File.Replace over the existing file
            Assert.Equal("h264", ConfigStore.ReadCodec(path));

            var back = h264 with { Encoding = h264.Encoding with { Codec = "JPEG" } };
            ConfigStore.Save(back, path);
            Assert.Equal("jpeg", ConfigStore.ReadCodec(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ReadCodec_MissingOrUnreadable_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "screenly-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ConfigStore.ReadCodec(Path.Combine(dir, "absent.json")));

            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ not json");
            Assert.Null(ConfigStore.ReadCodec(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ReadTargetFps_SeesFpsAfterAtomicSave()
    {
        var dir = Path.Combine(Path.GetTempPath(), "screenly-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "config.json");
        try
        {
            var cfg = new AppConfig();
            ConfigStore.Save(cfg, path); // first save: File.Move
            Assert.Equal(30, ConfigStore.ReadTargetFps(path));

            var fps60 = cfg with { Encoding = cfg.Encoding with { TargetFps = 60 } };
            ConfigStore.Save(fps60, path); // subsequent save: File.Replace over the existing file
            Assert.Equal(60, ConfigStore.ReadTargetFps(path));

            ConfigStore.Save(fps60 with { Encoding = fps60.Encoding with { TargetFps = 15 } }, path);
            Assert.Equal(15, ConfigStore.ReadTargetFps(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ReadTargetFps_MissingOrUnreadable_ReturnsNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), "screenly-cfg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ConfigStore.ReadTargetFps(Path.Combine(dir, "absent.json")));

            var path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, "{ not json");
            Assert.Null(ConfigStore.ReadTargetFps(path));

            File.WriteAllText(path, "{\"encoding\":{\"targetFps\":\"fast\"}}");
            Assert.Null(ConfigStore.ReadTargetFps(path));

            // Field absent (older config): the record's default applies.
            File.WriteAllText(path, "{\"encoding\":{\"codec\":\"h264\"}}");
            Assert.Equal(30, ConfigStore.ReadTargetFps(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
