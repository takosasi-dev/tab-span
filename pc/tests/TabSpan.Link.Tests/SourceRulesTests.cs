// ソースと参照の決まり: PC 側に HTTP のクライアントが無い(AC-38・INV-8)、TabSpan.Link が TabSpan.Platform を参照しない(AC-40・INV-14)。
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace TabSpan.Link.Tests;

public sealed partial class SourceRulesTests
{
    [GeneratedRegex(@"\b(HttpClient|WebClient|HttpWebRequest)\b")]
    private static partial Regex HttpClients();

    private static string SrcDir([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "src"));

    [Fact]
    public void AC38_PC_のソースに_HttpClient_WebClient_HttpWebRequest_が無い()
    {
        var files = Directory.EnumerateFiles(SrcDir(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(files);
        Assert.DoesNotContain(files, f => HttpClients().IsMatch(File.ReadAllText(f)));
    }

    [Fact]
    public void AC40_TabSpan_Link_は_TabSpan_Platform_を参照しない()
    {
        Assert.DoesNotContain(typeof(LinkServer).Assembly.GetReferencedAssemblies(), a => a.Name == "TabSpan.Platform");
        Assert.DoesNotContain("TabSpan.Platform.csproj", File.ReadAllText(Path.Combine(SrcDir(), "TabSpan.Link", "TabSpan.Link.csproj")), StringComparison.Ordinal);
    }
}
