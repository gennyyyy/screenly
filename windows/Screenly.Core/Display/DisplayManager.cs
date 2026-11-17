using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Screenly.Core.Display;

public sealed partial class ParsecVddDisplay : IVirtualDisplay, IDisposable
{
    private const uint VDD_IOCTL_ADD     = 0x0022e004;
    private const uint VDD_IOCTL_REMOVE  = 0x0022a008;
    private const uint VDD_IOCTL_UPDATE  = 0x0022a00c;
    private const uint VDD_IOCTL_VERSION = 0x0022e010;

    private static readonly Guid AdapterGuid = new("00b41627-04c4-429e-a26e-0265cf50c8fa");

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr hDevInfo, IntPtr devInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr hDevInfo, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr hDevInfo);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, ref OVERLAPPED lpOverlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResultEx(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, uint dwMilliseconds, bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(uint numPathArrayElements, IntPtr pathArray, uint numModeArrayElements, IntPtr modeArray, uint flags);

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;
    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x00000001;
    private const uint FILE_SHARE_WRITE = 0x00000002;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const uint FILE_FLAG_WRITE_THROUGH = 0x80000000; // note: overlaps GENERIC_READ value; used separately
    private const uint FILE_FLAG_NO_BUFFERING = 0x20000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OVERLAPPED
    {
        public IntPtr Internal;
        public IntPtr InternalHigh;
        public uint Offset;
        public uint OffsetHigh;
        public IntPtr hEvent;
    }

    private IntPtr _handle = IntPtr.Zero;
    private IntPtr _syncHandle = IntPtr.Zero;
    private readonly Dictionary<int, int> _displays = new();
    private int _nextId = 1;
    private Thread? _pingThread;
    private volatile bool _running;

    private IntPtr OpenHandle(bool overlapped = true)
    {
        // Always use synchronous (non-overlapped) handle — device may not support overlapped I/O.
        if (_syncHandle != IntPtr.Zero) return _syncHandle;
        var path = @"\\?\ROOT#DISPLAY#0000#{00b41627-04c4-429e-a26e-0265cf50c8fa}";
        var sh = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (sh != new IntPtr(-1)) { _syncHandle = sh; _handle = sh; return sh; }
        Log($"OpenHandle failed: path={path} err={Marshal.GetLastWin32Error()}");
        return IntPtr.Zero;
    }

    // IoControl with overlapped pattern per docs; synchronous=true uses a non-overlapped handle
    private bool IoControl(uint code, byte[]? inBuf, byte[]? outBuf, uint timeoutMs, int inBufferSize = 32, bool synchronous = true)
    {
        if (inBufferSize < 1) inBufferSize = 1;
        var h = OpenHandle(!synchronous);
        if (h == IntPtr.Zero) return false;

        var inBuffer = new byte[inBufferSize];
        if (inBuf != null) Buffer.BlockCopy(inBuf, 0, inBuffer, 0, Math.Min(inBuf.Length, inBufferSize));
        var inPtr = Marshal.AllocHGlobal(inBufferSize);
        var outPtr = outBuf != null ? Marshal.AllocHGlobal(Math.Max(1, outBuf.Length)) : Marshal.AllocHGlobal(4);
        try
        {
            Marshal.Copy(inBuffer, 0, inPtr, inBufferSize);
            uint returned = 0;
            if (synchronous)
            {
                bool ok = DeviceIoControl(h, code, inPtr, (uint)inBufferSize, outPtr, (uint)(outBuf?.Length ?? 4), out returned, IntPtr.Zero);
                if (!ok) return false;
                if (outBuf != null && returned > 0)
                    Marshal.Copy(outPtr, outBuf, 0, (int)Math.Min(returned, (uint)outBuf.Length));
                return true;
            }
            var ov = new OVERLAPPED();
            DeviceIoControl(h, code, inPtr, (uint)inBufferSize, outPtr, (uint)(outBuf?.Length ?? 4), out returned, ref ov);
            if (!GetOverlappedResultEx(h, ref ov, out returned, timeoutMs, false))
            {
                CancelIoEx(h, ref ov);
                GetOverlappedResult(h, ref ov, out returned, true);
                return false;
            }
            if (outBuf != null && returned > 0)
                Marshal.Copy(outPtr, outBuf, 0, (int)Math.Min(returned, (uint)outBuf.Length));
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inPtr);
            Marshal.FreeHGlobal(outPtr);
        }
    }

    private static string AppDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenly");
    private static string IndicesPath => Path.Combine(AppDataDir, "vdd-indices.txt");
    private static string LegacyIndexPath => Path.Combine(AppDataDir, "vdd-index.txt");

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(Path.Combine(AppDataDir, "engine.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private void RemovePersistedIndex(int index)
    {
        try
        {
            if (!File.Exists(IndicesPath)) return;
            var lines = File.ReadAllLines(IndicesPath).Where(l => int.TryParse(l.Trim(), out var v) && v != index).ToArray();
            if (lines.Length == 0) File.Delete(IndicesPath);
            else File.WriteAllLines(IndicesPath, lines);
        }
        catch { }
    }

    public bool EnsureRegistryPreset(int width, int height, int refreshHz)
    {
        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Parsec\vdd", true)
                ?? Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Parsec\vdd", true);
            if (baseKey == null)
            {
                Log("EnsureRegistryPreset: failed to open/create base key");
                return false;
            }
            int slot = -1;
            for (int i = 0; i <= 4; i++)
            {
                using var sub = baseKey.OpenSubKey(i.ToString());
                if (sub == null)
                {
                    if (slot == -1) slot = i;
                    continue;
                }
                var wVal = sub.GetValue("width") ?? sub.GetValue("w");
                var hVal = sub.GetValue("height") ?? sub.GetValue("h");
                var hzVal = sub.GetValue("hz");
                if (wVal != null && hVal != null && hzVal != null &&
                    Convert.ToInt32(wVal) == width && Convert.ToInt32(hVal) == height && Convert.ToInt32(hzVal) == refreshHz)
                {
                    slot = i;
                    break;
                }
                if ((sub.GetValue("width") == null && sub.GetValue("w") == null) && slot == -1)
                {
                    slot = i;
                }
            }
            if (slot == -1) slot = 0;
            using var key = baseKey.CreateSubKey(slot.ToString(), true);
            key.SetValue("width", width, RegistryValueKind.DWord);
            key.SetValue("height", height, RegistryValueKind.DWord);
            key.SetValue("hz", refreshHz, RegistryValueKind.DWord);
            key.SetValue("w", width, RegistryValueKind.DWord);
            key.SetValue("h", height, RegistryValueKind.DWord);
            Log($"EnsureRegistryPreset: slot {slot} set to {width}x{height}@{refreshHz}Hz success");
            return true;
        }
        catch (Exception ex)
        {
            Log($"EnsureRegistryPreset: FAILED {ex.Message}");
            return false;
        }
    }

    public int DisplayCount => _displays.Count;

    /// <summary>
    /// Remove stale VDD displays left by previous (crashed) runs.
    /// Call once at engine startup, in the background, so normal CreateAsync
    /// only creates a single display without churn.
    /// </summary>
    public void CleanupStaleIndices()
    {
        try
        {
            var indices = new List<int>();
            if (File.Exists(IndicesPath))
            {
                foreach (var line in File.ReadAllLines(IndicesPath))
                    if (int.TryParse(line.Trim(), out var v)) indices.Add(v);
            }
            if (File.Exists(LegacyIndexPath))
            {
                if (int.TryParse(File.ReadAllText(LegacyIndexPath).Trim(), out var v)) indices.Add(v);
            }
            var remaining = new List<int>(indices.Distinct());
            foreach (var oldIdx in indices.Distinct().ToList())
            {
                bool removed = false;
                var buf = new byte[32];
                buf[0] = (byte)((oldIdx >> 8) & 0xFF);
                buf[1] = (byte)(oldIdx & 0xFF);
                if (IoControl(VDD_IOCTL_REMOVE, buf, null, 5000, 32, false))
                {
                    removed = true;
                }
                else
                {
                    var buf2 = new byte[2];
                    buf2[0] = buf[0]; buf2[1] = buf[1];
                    if (IoControl(VDD_IOCTL_REMOVE, buf2, null, 5000, 2, false))
                        removed = true;
                }
                Log($"VDD REMOVE (stale cleanup) index {oldIdx}: {(removed ? "success" : "FAILED")} Win32Error={Marshal.GetLastWin32Error()}");
                if (removed) remaining.Remove(oldIdx);
            }
            try
            {
                if (remaining.Count == 0)
                {
                    try { File.Delete(IndicesPath); } catch { }
                    try { File.Delete(LegacyIndexPath); } catch { }
                }
                else
                {
                    File.WriteAllLines(IndicesPath, remaining.Select(i => i.ToString()));
                    try { File.Delete(LegacyIndexPath); } catch { }
                }
            }
            catch { }
        }
        catch { }
    }

    public Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default)
    {
        var h = OpenHandle();
        if (h == IntPtr.Zero) throw new InvalidOperationException("Parsec VDD device not found (driver not installed?)");
        // Clean up stale displays from previous (crashed) runs synchronously
        // before creating a new one, to prevent ghost VDD accumulation.
        if (_displays.Count == 0)
        {
            try { CleanupStaleIndices(); } catch { }
        }
        // Ensure registry preset so driver creates display at requested resolution
        try
        {
            if (!EnsureRegistryPreset(width, height, refreshHz))
                Log("CreateAsync: EnsureRegistryPreset failed; continuing with fallback default res");
        }
        catch (Exception ex) { Log($"CreateAsync: EnsureRegistryPreset threw: {ex.Message}"); }

        // Parsec VDD ADD ioctl: all-zeros creates a display at default resolution.
        // Resolution is then set via ChangeDisplaySettingsEx in the engine.
        var inBuf = new byte[32];
        var outBuf = new byte[4];
        if (!IoControl(VDD_IOCTL_ADD, inBuf, outBuf, 5000))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        int index = BitConverter.ToInt32(outBuf, 0);
        if (index < 0 || index > 15) throw new InvalidOperationException($"Invalid display index: {index}");
        // Persist ALL created indices (append)
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.AppendAllText(IndicesPath, index.ToString() + Environment.NewLine);
        }
        catch { }
        Log($"VDD created index {index} ({width}x{height})");
        try { IoControl(VDD_IOCTL_UPDATE, new byte[32], null, 1000); } catch { }
        var handle = new DisplayHandle(_nextId++);
        _displays[handle.Id] = index;
        StartPing();
        return Task.FromResult(handle);
    }

    public async Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default)
    {
        if (!_displays.TryGetValue(handle.Id, out var index)) return;
        // Deactivate the display from the desktop topology before removal
        try { SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, 0x00000001 | 0x00000080); } catch { } // SDC_TOPOLOGY_INTERNAL | SDC_APPLY
        await Task.Delay(500);
        // Stop ping thread before removal (UPDATE ioctls interfere)
        StopPing();
        await Task.Delay(200);
        // Try multiple buffer formats: 32-byte, 2-byte, 4-byte
        bool removed = false;
        var formats = new (byte[] buf, int size)[]
        {
            (new byte[] { (byte)((index >> 8) & 0xFF), (byte)(index & 0xFF), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, 32),
            (new byte[] { (byte)((index >> 8) & 0xFF), (byte)(index & 0xFF) }, 2),
            (new byte[] { (byte)(index & 0xFF), (byte)((index >> 8) & 0xFF) }, 2), // little-endian
        };
        foreach (var (buf, size) in formats)
        {
            if (removed) break;
            for (int attempt = 0; attempt < 2 && !removed; attempt++)
            {
                if (attempt > 0) await Task.Delay(300);
                StopPing();
                removed = IoControl(VDD_IOCTL_REMOVE, buf, null, 5000, size, synchronous: true);
                var werr2 = Marshal.GetLastWin32Error();
                Log($"VDD REMOVE index {index} fmt={size}be attempt {attempt + 1}: {(removed ? "success" : "FAILED")} Win32Error={werr2}");
            }
        }
        var werr = Marshal.GetLastWin32Error();
        if (removed)
        {
            _displays.Remove(handle.Id);
            RemovePersistedIndex(index);
            Log($"VDD removed index {index}");
            await PingAsync();
            if (_displays.Count > 0) StartPing();
        }
        else
        {
            // Removal failed: drop from active set so stale cleanup can handle the orphan later.
            _displays.Remove(handle.Id);
            Log($"VDD RemoveAsync FAILED for index {index} (Win32Error={werr}); orphaned index persisted");
            throw new System.ComponentModel.Win32Exception(werr);
        }
    }

    public Task SetModeAsync(DisplayHandle handle, int width, int height, int refreshHz, CancellationToken ct = default)
    {
        // Parsec VDD doesn't have a set-mode IOCTL; mode is fixed at creation.
        // Ping to keep alive.
        return PingAsync(ct);
    }

    public Task PingAsync(CancellationToken ct = default)
    {
        IoControl(VDD_IOCTL_UPDATE, new byte[32], null, 1000);
        return Task.CompletedTask;
    }

    public IEnumerable<DisplayInfo> Enumerate() => _displays.Select(kv => new DisplayInfo(kv.Key, $"Parsec VDD {kv.Key}", 0, 0, 0, true));

    private void StartPing()
    {
        if (_pingThread != null) return;
        _running = true;
        _pingThread = new Thread(() =>
        {
            while (_running)
            {
                try { PingAsync().GetAwaiter().GetResult(); } catch { }
                Thread.Sleep(100);
            }
        }) { IsBackground = true, Name = "vdd-ping" };
        _pingThread.Start();
    }

    private void StopPing()
    {
        _running = false;
        _pingThread?.Join(500);
        _pingThread = null;
    }

    public void Dispose()
    {
        StopPing();
        foreach (var kv in _displays.Keys.ToList())
            try { RemoveAsync(new DisplayHandle(kv)).GetAwaiter().GetResult(); } catch { }
        try { File.Delete(IndicesPath); } catch { }
        try { File.Delete(LegacyIndexPath); } catch { }
        if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
        if (_syncHandle != IntPtr.Zero) { CloseHandle(_syncHandle); _syncHandle = IntPtr.Zero; }
    }
}
