// スレッドのホットキー(RegisterHotKey の hWnd = NULL。WM_HOTKEY はこの UI スレッドの列に来る)。FR-23 のタッチの切り替えに使う。
// "Ctrl+Alt+T" の形の文字を読む。読めない・登録できない(ほかのアプリが使っている)なら null。
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace TabSpan.App;

public sealed class HotKey : IDisposable
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int Id = 0x5453; // 'TS'

    private readonly Action _pressed;

    private HotKey(Action pressed)
    {
        _pressed = pressed;
        ComponentDispatcher.ThreadFilterMessage += OnMessage;
    }

    public static HotKey? TryRegister(string text, Action pressed)
    {
        if (Parse(text) is not { } k || !RegisterHotKey(IntPtr.Zero, Id, k.Modifiers | ModNoRepeat, k.VirtualKey))
        {
            return null;
        }

        return new HotKey(pressed);
    }

    internal static (uint Modifiers, uint VirtualKey)? Parse(string text)
    {
        uint mods = 0;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null; // 修飾キーなしの 1 文字は横取りが過ぎるので受けない
        }

        foreach (var m in parts[..^1])
        {
            mods |= m.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => ModControl,
                "ALT" => ModAlt,
                "SHIFT" => ModShift,
                "WIN" => ModWin,
                _ => 0u,
            };
        }

        try
        {
            var key = (Key)new KeyConverter().ConvertFromInvariantString(parts[^1])!;
            var vk = KeyInterop.VirtualKeyFromKey(key);
            return mods == 0 || vk == 0 ? null : (mods, (uint)vk);
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private void OnMessage(ref MSG msg, ref bool handled)
    {
        if (msg.message == WmHotKey && (int)msg.wParam == Id)
        {
            handled = true;
            _pressed();
        }
    }

    public void Dispose()
    {
        ComponentDispatcher.ThreadFilterMessage -= OnMessage;
        UnregisterHotKey(IntPtr.Zero, Id);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
