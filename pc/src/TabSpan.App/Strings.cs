// 画面の文言を Strings.ja.resx から引く(FR-30)。英語の resx はまだ無いので、どの UI のカルチャでも ja を明示して引く。
// XAML からは {local:Str キー} で引く。
using System.Globalization;
using System.Resources;
using System.Windows.Markup;

[assembly: NeutralResourcesLanguage("ja", UltimateResourceFallbackLocation.Satellite)]

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
