namespace MidiCore;

/// <summary>
/// MIDI OUT の先につながっている音源を、問い合わせの SysEx への返事で見分ける。
/// 返事は音源の MIDI OUT → PC の MIDI IN に来るので、MIDI IN がつながっている音源だけ見分けられる。
/// </summary>
public static class Detect
{
    static byte[] RolandRq1(byte model, byte a0, byte a1, byte a2, int size)
    {
        var body = new byte[] { a0, a1, a2, (byte)((size >> 14) & 0x7F), (byte)((size >> 7) & 0x7F), (byte)(size & 0x7F) };
        var m = new List<byte> { 0xF0, 0x41, 0x10, model, 0x11 };
        m.AddRange(body);
        m.Add(RolandSysex.Checksum(body));
        m.Add(0xF7);
        return m.ToArray();
    }

    /// <summary>順に送る問い合わせ（どれも音を変えない読み出しだけ）</summary>
    public static readonly byte[][] Probes =
    {
        // ユニバーサルの機種問い合わせ（SC-88Pro・MU シリーズなどが答える）
        new byte[] { 0xF0, 0x7E, 0x7F, 0x06, 0x01, 0xF7 },
        // GS：マスターチューン（40 00 00 から 4 バイト）を読む
        RolandRq1(0x42, 0x40, 0x00, 0x00, 4),
        // MT-32：システム領域（10 00 00 から 1 バイト）を読む
        RolandRq1(0x16, 0x10, 0x00, 0x00, 1),
        // XG：システムのパラメータ（00 00 00）を読む
        new byte[] { 0xF0, 0x43, 0x30, 0x4C, 0x00, 0x00, 0x00, 0xF7 },
        // DX7 / TX7：ボイスのダンプ要求
        new byte[] { 0xF0, 0x43, 0x20, 0x00, 0xF7 },
    };

    /// <summary>受け取ったバイト列から SysEx（F0〜F7）を切り出す</summary>
    public static List<byte[]> SplitSysex(IEnumerable<byte> stream)
    {
        var o = new List<byte[]>();
        List<byte>? cur = null;
        foreach (var b in stream)
        {
            if (b == 0xF0) cur = new List<byte> { b };
            else if (cur != null)
            {
                if (b >= 0xF8) continue; // リアルタイムメッセージは挟まってもよい
                cur.Add(b);
                if (b == 0xF7)
                {
                    o.Add(cur.ToArray());
                    cur = null;
                }
                else if (b > 0xF7 || (b >= 0x80 && b != 0xF7)) cur = null; // 途中で切れた
            }
        }
        return o;
    }

    /// <summary>返事の SysEx から音源の種類を決める。見分けがつかなければ null</summary>
    public static Port? Classify(IEnumerable<byte[]> replies)
    {
        bool tx = false, mt = false, xg = false, gs = false;
        foreach (var m in replies)
        {
            if (m.Length < 5 || m[0] != 0xF0) continue;
            if (m[1] == 0x43)
            {
                int kind = m[2] & 0xF0;
                // DX7/TX7 のボイスダンプ（F0 43 0n 00 … / 09 …）
                if (kind == 0x00 && m[3] is 0x00 or 0x09) tx = true;
                // XG のパラメータの返事（F0 43 1n 4C …）・バルク（0n 4C）
                if (kind is 0x10 or 0x00 && m[3] == 0x4C) xg = true;
            }
            else if (m[1] == 0x41 && m.Length > 4 && m[4] == 0x12)
            {
                // Roland の DT1（データの返事）
                if (m[3] == 0x16) mt = true;
                else if (m[3] is 0x42 or 0x45) gs = true;
            }
            else if (m[1] == 0x7E && m.Length > 5 && m[3] == 0x06 && m[4] == 0x02)
            {
                // 機種の返事（F0 7E dev 06 02 メーカー …）
                if (m[5] == 0x41) gs = true;
                else if (m[5] == 0x43) xg = true;
            }
        }
        if (tx) return Port.Other;
        if (mt) return Port.Mt32;
        if (xg) return Port.Xg;
        if (gs) return Port.Main;
        return null;
    }
}
