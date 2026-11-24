// H.264 の Annex B の流れ(開始コード 00 00 01 / 00 00 00 01 付き)を、開始コードを除いた NAL の列に分ける。
// 最初の開始コードより前のバイトと、NAL の後ろの 0(trailing_zero_8bits)は捨てる。中身の無い NAL は返さない。
namespace TabSpan.Core;

public static class AnnexB
{
    public static IReadOnlyList<ReadOnlyMemory<byte>> SplitNals(ReadOnlyMemory<byte> annexB)
    {
        var s = annexB.Span;
        var nals = new List<ReadOnlyMemory<byte>>();
        var start = -1; // 今の NAL の先頭(開始コードの次)
        var i = 0;
        while (i + 2 < s.Length)
        {
            if (s[i] == 0 && s[i + 1] == 0 && s[i + 2] == 1)
            {
                if (start >= 0)
                {
                    Add(annexB, start, i, nals);
                }

                i += 3;
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start >= 0)
        {
            Add(annexB, start, s.Length, nals);
        }

        return nals;
    }

    private static void Add(ReadOnlyMemory<byte> data, int start, int end, List<ReadOnlyMemory<byte>> nals)
    {
        var s = data.Span;
        while (end > start && s[end - 1] == 0)
        {
            end--; // 4 バイトの開始コードの先頭の 00 と trailing_zero_8bits
        }

        if (end > start)
        {
            nals.Add(data[start..end]);
        }
    }
}
