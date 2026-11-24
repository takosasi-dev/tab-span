// Windows の起動時に開始する(FR-25): HKCU\...\Run の値 TabSpan を書く・消す。
// 書き込みは IRunKey の裏に置き、確かめは偽物で行う(本物のレジストリに書かない)。
using Microsoft.Win32;

namespace TabSpan.App;

public interface IRunKey
{
    string? Get();

    void Set(string command);

    void Delete();
}

public sealed class RegistryRunKey : IRunKey
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "TabSpan";

    public string? Get()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void Set(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, command);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

public static class StartupRegistration
{
    /// <summary>起動時はトレイだけで始める印(App が見る)。</summary>
    public const string TrayArgument = "--tray";

    public static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArgument}";

    /// <summary>真なら値を書き(違っていれば書き直す)、偽なら消す。</summary>
    public static void Apply(IRunKey key, bool enabled, string exePath)
    {
        if (!enabled)
        {
            key.Delete();
            return;
        }

        var command = CommandFor(exePath);
        if (key.Get() != command)
        {
            key.Set(command);
        }
    }
}
