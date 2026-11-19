namespace Screenly.Core.Capture;

// DEVMODE.dmDisplayOrientation (DMDO_DEFAULT..DMDO_270) maps 1:1 onto the value the engine puts on
// the wire (HELLO_ACK.config, CONFIG, status.json): 0 = 0°, 1 = 90°, 2 = 180°, 3 = 270° clockwise.
public static class DisplayOrientation
{
    public const int Default = 0;
    public const int Rotate90 = 1;
    public const int Rotate180 = 2;
    public const int Rotate270 = 3;

    // Drivers that do not report orientation leave the field at 0; anything outside the four
    // DMDO_* values is treated as unrotated. Used for both DEVMODE reads and wire input.
    public static int Normalize(int dmDisplayOrientation) =>
        dmDisplayOrientation is >= Default and <= Rotate270 ? dmDisplayOrientation : Default;
}
