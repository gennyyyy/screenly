using System.Runtime.InteropServices;

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
    private static extern bool GetOverlappedResultEx(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, uint dwMilliseconds, bool bAlertable);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr hFile, ref OVERLAPPED lpOverlapped, out uint lpNumberOfBytesTransferred, bool bWait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr hFile, ref OVERLAPPED lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

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
    private readonly Dictionary<int, int> _displays = new();
    private int _nextId = 1;
    private Thread? _pingThread;
    private volatile bool _running;

    private IntPtr OpenHandle()
    {
        if (_handle != IntPtr.Zero) return _handle;

        var path = @"\\?\ROOT#DISPLAY#0000#{00b41627-04c4-429e-a26e-0265cf50c8fa}";
        uint flags = FILE_FLAG_OVERLAPPED;
        var h = CreateFile(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, flags, IntPtr.Zero);
        if (h != new IntPtr(-1))
        {
            _handle = h;
            return h;
        }
        return IntPtr.Zero;
    }

    // IoControl with overlapped pattern per docs
    private bool IoControl(uint code, byte[]? inBuf, byte[]? outBuf, uint timeoutMs)
    {
        var h = OpenHandle();
        if (h == IntPtr.Zero) return false;

        // Ensure 32-byte input buffer
        var inBuffer = new byte[32];
        if (inBuf != null) Buffer.BlockCopy(inBuf, 0, inBuffer, 0, Math.Min(inBuf.Length, 32));
        var inPtr = Marshal.AllocHGlobal(32);
        var outPtr = outBuf != null ? Marshal.AllocHGlobal(outBuf.Length) : Marshal.AllocHGlobal(4);
        var ov = new OVERLAPPED();
        try
        {
            Marshal.Copy(inBuffer, 0, inPtr, 32);
            uint returned = 0;
            // Call DeviceIoControl — ignore sync return
            DeviceIoControl(h, code, inPtr, 32, outPtr, (uint)(outBuf?.Length ?? 4), out returned, ref ov);
            // Wait via GetOverlappedResultEx
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

    public Task<DisplayHandle> CreateAsync(int width, int height, int refreshHz, CancellationToken ct = default)
    {
        var h = OpenHandle();
        if (h == IntPtr.Zero) throw new InvalidOperationException("Parsec VDD device not found (driver not installed?)");
        // Clean up stale display from a previous (crashed) run
        var idxFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenly", "vdd-index.txt");
        try
        {
            if (File.Exists(idxFile))
            {
                var oldIdx = int.Parse(File.ReadAllText(idxFile).Trim());
                var buf = new byte[32];
                BitConverter.GetBytes(oldIdx).CopyTo(buf, 1);
                IoControl(VDD_IOCTL_REMOVE, buf, null, 2000);
                File.Delete(idxFile);
            }
        }
        catch { }
        var outBuf = new byte[4];
        if (!IoControl(VDD_IOCTL_ADD, new byte[32], outBuf, 5000))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        int index = BitConverter.ToInt32(outBuf, 0);
        if (index < 0 || index > 15) throw new InvalidOperationException($"Invalid display index: {index}");
        // Persist index so next run can clean up if this process is killed
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenly");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "vdd-index.txt"), index.ToString());
        }
        catch { }
        var handle = new DisplayHandle(_nextId++);
        _displays[handle.Id] = index;
        StartPing();
        return Task.FromResult(handle);
    }

    public Task RemoveAsync(DisplayHandle handle, CancellationToken ct = default)
    {
        if (!_displays.TryGetValue(handle.Id, out var index)) return Task.CompletedTask;
        var inBuf = new byte[32];
        BitConverter.GetBytes(index).CopyTo(inBuf, 1); // monitor_index at [1..4]
        IoControl(VDD_IOCTL_REMOVE, inBuf, null, 1000);
        _displays.Remove(handle.Id);
        if (_displays.Count == 0) StopPing();
        return Task.CompletedTask;
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
            RemoveAsync(new DisplayHandle(kv)).GetAwaiter().GetResult();
        try { File.Delete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenly", "vdd-index.txt")); } catch { }
        if (_handle != IntPtr.Zero) { CloseHandle(_handle); _handle = IntPtr.Zero; }
    }
}
