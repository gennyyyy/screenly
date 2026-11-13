using System.Runtime.InteropServices;

namespace Screenly.Core.Input;

[StructLayout(LayoutKind.Sequential)]
public struct TouchEvent
{
    public string Type; // down/move/up/cancel
    public int PointerId;
    public float X;
    public float Y;
    public float Pressure;
}

public static class InputInjector
{
    // Windows VK / input injection via SendInput.
    // Multi-touch: build INPUT[] array and call SendInput once.
    public static void InjectTouch(IEnumerable<TouchEvent> events, int streamW, int streamH, int displayW, int displayH)
    {
        // TODO: scale coords (x*displayW/streamW), build INPUT structs with MOUSEEVENTF_*, call SendInput.
        // Pressure ignored in v1 (SendInput mouse has no pressure); use RealTimeStylus for pen pressure.
        foreach (var e in events)
        {
            if (streamW <= 0 || streamH <= 0) return;
            int px = (int)(e.X * displayW / (float)streamW);
            int py = (int)(e.Y * displayH / (float)streamH);
            // placeholder: no-op until P/Invoke wired
            _ = (px, py);
        }
    }

    public static void InjectKey(int vk, bool down)
    {
        // TODO: SendInput with INPUT_KEYBOARD.
        _ = (vk, down);
    }
}
