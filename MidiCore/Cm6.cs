namespace MidiCore;

/// <summary>
/// レコンポーザの CM6 ファイル（MT-32 / CM-32L / CM-64 の音源設定）を読み、音源へ送る SysEx 列にする。
/// CM-64 の PCM 部（CM-32P）の設定（0x4E34 以降）は送らない。
/// </summary>
public static class Cm6
{
    const int Len = 0x5849;
    const int SystemAt = 0x80;     // 10 00 00、23 バイト
    const int PatchTemp = 0xA0;    // 03 00 00、パート 1〜8 × 16 バイト
    const int Rhythm = 0x130;      // 03 01 10、85 鍵 × 4 バイト
    const int PatchMem = 0xA34;    // 05 00 00、128 × 8 バイト
    const int TimbreMem = 0xE34;   // 08 00 00、64 × 256 バイト（中身は先頭 246 バイト）
    const int Timbre = 246;

    /// <summary>CM6 ファイルか。0x10 からの機種名は "R MT-32"・"R CM-64"・"R CM-32L" など</summary>
    public static bool IsCm6(byte[] d) => d.Length >= Len && Smf.StartsWith(d, 0, "COME ON MUSIC") && Smf.StartsWith(d, 0x10, "R ");

    static void PushChunks(List<byte[]> o, (byte, byte, byte) b, byte[] d, int at, int len, int chunk)
    {
        for (int i = 0; i * chunk < len; i++)
            o.Add(RolandSysex.Mt32Dt1(RolandSysex.AddrAdd(b, i * chunk), d.AsSpan(at + i * chunk, Math.Min(chunk, len - i * chunk))));
    }

    /// <summary>CM6 の中身を、音源へ送る順の SysEx 列にする（名前が空のユーザー音色は送らない）</summary>
    public static List<byte[]> ToSysex(byte[] d)
    {
        if (!IsCm6(d)) throw new MidiFormatException("CM6 ファイルではありません");
        var o = new List<byte[]>();
        int n = 0;
        // 1) ユーザー音色とパッチ
        for (int slot = 0; slot < 64; slot++)
        {
            int at = TimbreMem + slot * 256;
            var t = d.AsSpan(at, Timbre).ToArray();
            if (t.Take(10).Any(c => c != (byte)' ' && c != 0))
            {
                RolandSysex.PushMt32Timbre(o, RolandSysex.AddrAdd((0x08, 0, 0), slot * 256), t);
                n++;
            }
        }
        PushChunks(o, (0x05, 0, 0), d, PatchMem, 128 * 8, 128);
        // 2) リズムの割り当てとシステム（リバーブ・パーシャルリザーブ・受信チャンネル）
        PushChunks(o, (0x03, 0x01, 0x10), d, Rhythm, 85 * 4, 128);
        o.Add(RolandSysex.Mt32Dt1((0x10, 0, 0), d.AsSpan(SystemAt, 23)));
        // 3) 各パートの今のパッチ
        PushChunks(o, (0x03, 0, 0), d, PatchTemp, 8 * 16, 16);
        // 4) 確認用：MT-32 本体の液晶に「Tone Set N」を表示する
        o.Add(RolandSysex.Mt32Display($"Tone Set {n}"));
        return o;
    }

    /// <summary>ファイル名を Shift_JIS のバイト列にする（ASCII と半角カナだけ。ほかの文字があれば null）</summary>
    static byte[]? SjisName(string s)
    {
        var o = new List<byte>();
        foreach (char c in s)
        {
            if (c >= 0x20 && c <= 0x7E) o.Add((byte)char.ToUpperInvariant(c));
            else if (c >= 0xFF61 && c <= 0xFF9F) o.Add((byte)(c - 0xFF61 + 0xA1));
            else return null;
        }
        return o.ToArray();
    }

    /// <summary>曲ファイルと同じフォルダから CM6 を探す。見つからなければ、曲と同じ名前の .CM6 を探す</summary>
    public static string? FindFor(string songPath, byte[]? name)
    {
        var dir = Path.GetDirectoryName(songPath);
        if (dir == null || !Directory.Exists(dir)) return null;
        var entries = Directory.GetFiles(dir);
        if (name != null)
        {
            var want = name.Select(b => b is >= (byte)'a' and <= (byte)'z' ? (byte)(b - 32) : b).ToArray();
            var hit = entries.FirstOrDefault(p => SjisName(Path.GetFileName(p)) is byte[] n && n.SequenceEqual(want));
            if (hit != null) return hit;
        }
        var stem = Path.GetFileNameWithoutExtension(songPath).ToUpperInvariant();
        return entries.FirstOrDefault(p =>
            string.Equals(Path.GetExtension(p), ".cm6", StringComparison.OrdinalIgnoreCase)
            && Path.GetFileNameWithoutExtension(p).ToUpperInvariant() == stem);
    }
}
