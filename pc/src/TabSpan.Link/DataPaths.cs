// PC 側のデータの置き場(設定・identity・台帳と、ログ)を決める。環境変数 TABSPAN_DATA_DIR があれば両方をその下に置き換える。
// ファイルの書き込みは、同じフォルダの一時ファイルに書いてから置き換える(仕様 §9.2)。
namespace TabSpan.Link;

public sealed record DataPaths(string DataDir, string LogDir)
{
    public const string OverrideVariable = "TABSPAN_DATA_DIR";

    public string Settings => Path.Combine(DataDir, "settings.json");

    public string Identity => Path.Combine(DataDir, "identity.bin");

    public string Devices => Path.Combine(DataDir, "devices.json");

    /// <summary>外した仮想ディスプレイと、TabSpan が付けた物(§9.2。Platform の VirtualDisplayKeeper が読み書きする)。</summary>
    public string State => Path.Combine(DataDir, "state.json");

    /// <summary>既定は %APPDATA%\TabSpan と %LOCALAPPDATA%\TabSpan\logs。TABSPAN_DATA_DIR があれば &lt;dir&gt; と &lt;dir&gt;\logs。</summary>
    public static DataPaths Resolve()
    {
        var dir = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return new DataPaths(dir, Path.Combine(dir, "logs"));
        }

        return new DataPaths(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TabSpan"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TabSpan", "logs"));
    }
}

public static class AtomicFile
{
    /// <summary>一時ファイルに書いてから置き換える。フォルダが無ければ作る。</summary>
    public static void Write(string path, ReadOnlySpan<byte> bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        using (var file = File.Create(temp))
        {
            file.Write(bytes);
        }

        File.Move(temp, path, overwrite: true);
    }

    /// <summary>読めなかったファイルを &lt;名前&gt;.broken-&lt;yyyyMMdd-HHmmss&gt;&lt;拡張子&gt; に名前を変えて残し、その場所を返す。</summary>
    public static string SetAside(string path, DateTimeOffset now)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var broken = Path.Combine(dir, $"{Path.GetFileNameWithoutExtension(path)}.broken-{now.ToLocalTime():yyyyMMdd-HHmmss}{Path.GetExtension(path)}");
        File.Move(path, broken, overwrite: true);
        return broken;
    }
}
