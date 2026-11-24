// 画面の文言を引く(FR-30)。日本語は Strings.resx(本体の DLL)、英語は Strings.en.resx(en の Satellite)。XAML からは {local:Str キー}。
// 言語は起動時に UseLanguage で 1 回だけ決める(auto は OS の UI の言語が ja なら日本語、それ以外は英語)。CurrentUICulture は書き換えない。
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
    private static readonly CultureInfo English = CultureInfo.GetCultureInfo("en");

    /// <summary>今の言語(ja か en)。</summary>
    public static CultureInfo Culture { get; private set; } = Resolve("auto", CultureInfo.CurrentUICulture);

    /// <summary>設定の language(auto / ja / en)で言語を決める。窓を作る前に呼ぶ。</summary>
    public static void UseLanguage(string setting) => Culture = Resolve(setting, CultureInfo.CurrentUICulture);

    public static CultureInfo Resolve(string setting, CultureInfo uiCulture) => setting switch
    {
        "ja" => Japanese,
        "en" => English,
        _ => uiCulture.TwoLetterISOLanguageName == "ja" ? Japanese : English,
    };

    public static string Get(string key) => Manager.GetString(key, Culture) ?? $"[{key}]";

    public static string Format(string key, params object[] args) => string.Format(Culture, Get(key), args);

    /// <summary>確かめ用: その言語の表だけ(親へ戻らない)。</summary>
    internal static ResourceSet? SetOf(CultureInfo culture) => Manager.GetResourceSet(culture, createIfNotExists: true, tryParents: false);
}

[MarkupExtensionReturnType(typeof(string))]
public sealed class StrExtension(string key) : MarkupExtension
{
    public string Key { get; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) => Strings.Get(Key);
}
