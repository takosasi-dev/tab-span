// OS を 1 歩呼んで、値か、失敗の中身(例外の型・HRESULT・文)を文字にして出す。
// Probe は「どの呼び出しが通り、どれが通らないか」を出すのが仕事なので、ここだけは例外を広く受けて表示に変える。
using Windows.Win32.Foundation;

namespace TabSpan.Probe;

internal static class Attempt
{
    internal static string Read(Func<string> read)
    {
#pragma warning disable CA1031 // 握りつぶさず、失敗の中身をそのまま出力に出す
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            return Describe(ex);
        }
#pragma warning restore CA1031
    }

    /// <summary>1 歩を実行して「名前: OK」か「名前: 失敗: …」を出す。通ったら true。</summary>
    internal static bool Step(string name, Action act)
    {
        var result = Read(() =>
        {
            act();
            return "OK";
        });
        Console.WriteLine($"{name}: {result}");
        return result == "OK";
    }

    /// <summary>HRESULT を返す 1 歩の結果を出す。通ったら true。</summary>
    internal static bool Hr(string name, HRESULT hr)
    {
        Console.WriteLine($"{name}: {(hr.Succeeded ? "OK" : "失敗")} (HRESULT {hr})");
        return hr.Succeeded;
    }

    private static string Describe(Exception ex)
    {
        var text = $"失敗: {ex.GetType().Name} (HRESULT 0x{ex.HResult:X8}): {ex.Message}";
        return ex.InnerException is null ? text : $"{text} <- {Describe(ex.InnerException)["失敗: ".Length..]}";
    }
}
