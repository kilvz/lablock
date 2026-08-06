using System.Runtime.InteropServices;
using System.Text;

namespace LabLock.Client.Helpers;

public static class KeyMapper
{
    private const uint MAPVK_VK_TO_CHAR = 2;

    [DllImport("user32.dll")]
    private static extern int ToUnicode(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        StringBuilder pwszBuff,
        int cchBuff,
        uint wFlags);

    public static string Map(uint vkCode)
    {
        if (vkCode >= 0x41 && vkCode <= 0x5A)
        {
            var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
            var c = shift ? (char)vkCode : char.ToLowerInvariant((char)vkCode);
            return c.ToString();
        }

        if (vkCode >= 0x30 && vkCode <= 0x39)
            return ((char)vkCode).ToString();

        if (vkCode >= 0x70 && vkCode <= 0x7B)
            return $"[F{vkCode - 0x70 + 1}]";

        var sb = new StringBuilder(8);
        var keyState = new byte[256];
        var scanCode = (vkCode << 16) & 0xFFFF;
        if (ToUnicode(vkCode, scanCode, keyState, sb, sb.Capacity, 0) > 0)
            return sb.ToString();

        return vkCode switch
        {
            0x08 => "[Backspace]",
            0x09 => "[Tab]",
            0x0D => "[Enter]",
            0x1B => "[Esc]",
            0x20 => " ",
            0x2E => "[Delete]",
            0x24 => "[Home]",
            0x23 => "[End]",
            0x21 => "[PageUp]",
            0x22 => "[PageDown]",
            0x25 => "[Left]",
            0x26 => "[Up]",
            0x27 => "[Right]",
            0x28 => "[Down]",
            0x10 => "[Shift]",
            0x11 => "[Ctrl]",
            0x12 => "[Alt]",
            0x5B => "[Win]",
            _ => $"[0x{vkCode:X}]"
        };
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}
