namespace Screenly.Core.Display;

public readonly record struct DisplayHandle(int Id);
public readonly record struct DisplayInfo(int Id, string Name, int Width, int Height, int RefreshHz, bool IsVirtual);
public readonly record struct AttachedDisplay(DisplayHandle Handle, string DeviceName, string Identity, int Width, int Height, int RefreshHz);
public readonly record struct DisplayMode(int Width, int Height, int RefreshHz);

public enum LeaseState
{
    Free,
    Assigned,
    PendingRemove,
    Removed,
    Quarantined,
    Removing
}

// alias expected by plan docs
public enum PoolState
{
    Free,
    Assigned,
    PendingRemove,
    Removed,
    Quarantined,
    Removing
}

public static class PoolErrorCodes
{
    public const string NoDisplay = "NO_DISPLAY";
    public const string PoolDegraded = "POOL_DEGRADED";
    public const string DisplayMappingFailed = "DISPLAY_MAPPING_FAILED";
    public const string ModeUnsupported = "MODE_UNSUPPORTED";
}

public record DisplayLease
{
    public int Id { get; set; }
    public DisplayHandle Handle { get; set; }
    public string Identity { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public int Hz { get; set; }
    public int Orientation { get; set; }
    public LeaseState State { get; set; } = LeaseState.Free;
    public Guid? SessionId { get; set; }
    public string? ClientId { get; set; }
    public DateTimeOffset? RemoveDueAt { get; set; }
}

public interface IVirtualDisplay
{
    Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default);
    Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default);
    Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default);
    Task PingAsync(CancellationToken ct = default);
    IEnumerable<DisplayInfo> Enumerate();
}
