// 画面の文言を Strings.resx(日本語。本体の DLL に入る)から引く(FR-30)。英語の resx はまだ無いので、どの UI のカルチャでも ja を明示して引く。
// XAML からは {local:Str キー} で引く。
// 既定の言語の置き場を Satellite にすると、WPF の画面の定義(g.resources)まで別の DLL に探しに行き、起動で落ちる(v0.1.1 まで)。
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

[assembly: NeutralResourcesLanguage("ja")]

namespace TabSpan.App;

public static class Strings
{
    private static readonly ResourceManager Manager = new("TabSpan.App.Strings", typeof(Strings).Assembly);
    private static readonly CultureInfo Japanese = CultureInfo.GetCultureInfo("ja");

    public static string Get(string key) => Manager.GetString(key, Japanese) ?? $"[{key}]";

    public static string Format(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
