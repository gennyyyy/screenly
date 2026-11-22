using System.Text.Json;

namespace Screenly.Core.Config;

public sealed record AppConfig
{
    public int Version { get; init; } = 1;
    public int Port { get; init; } = 7543;
    public VirtualDisplayConfig VirtualDisplay { get; init; } = new();
    public EncodingConfig Encoding { get; init; } = new();
    public PairingConfig Pairing { get; init; } = new();
    public bool AutoStart { get; init; }
    public bool FirewallRuleAdded { get; init; }
}

public sealed record VirtualDisplayConfig(int Width = 1920, int Height = 1080, int RefreshHz = 60, bool DynamicResolution = true, int MaxWidth = 3840, int MaxHeight = 2160, int MaxDisplays = 4, int RemoveGraceSeconds = 5, bool PerClientDisplays = true);
public sealed record EncodingConfig(int BitrateKbps = 8000, int KeyframeIntervalSec = 2, int TargetFps = 30, string Codec = "jpeg", string? DumpPath = null);
public sealed record PairingConfig(bool PinEnabled = false, string? PinHash = null, string? Salt = null);
