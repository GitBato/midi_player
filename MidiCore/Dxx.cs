namespace MidiCore;

/// <summary>
/// TX7 / DX7 の音色ファイル（.DXX。MSX 用の TX7 音色ディスクの形式）を、32 音色バルクダンプ（VMEM）の SysEx にする。
/// 0〜38 がヘッダ、39〜4134 が 32 音色ぶんの VMEM（1 音色 128 バイト、+118 から 10 文字の音色名）。
/// 送る SysEx は F0 43 0n 09 20 00 ＜4096 バイト＞ チェックサム F7。TX7 は MEMORY PROTECT を OFF にしておく。
/// </summary>
public static class Dxx
{
    public const int VmemAt = 39;
    public const int VmemLen = 4096;
    const int VoiceLen = 128;
    const int NameAt = 118;

    public static bool IsDxx(string path, byte[] bytes) =>
        string.Equals(Path.GetExtension(path), ".dxx", StringComparison.OrdinalIgnoreCase) && bytes.Length >= VmemAt + VmemLen;

    /// <summary>32 音色の名前</summary>
    public static List<string> VoiceNames(byte[] b)
    {
        var o = new List<string>();
        if (b.Length < VmemAt + VmemLen) return o;
        for (int v = 0; v < 32; v++)
        {
            int at = VmemAt + v * VoiceLen + NameAt;
            o.Add(new string(b.Skip(at).Take(10).Select(c => c >= 0x20 && c < 0x7F ? (char)c : ' ').ToArray()).TrimEnd());
        }
        return o;
    }

    /// <summary>32 音色バルクダンプの SysEx（ch は 0〜15）</summary>
    public static byte[] ToSysex(byte[] bytes, int ch)
    {
        if (bytes.Length < VmemAt + VmemLen) throw new MidiFormatException("DXX ファイルが短すぎます");
        var v = bytes.AsSpan(VmemAt, VmemLen);
        int bad = v.IndexOfAnyInRange((byte)0x80, (byte)0xFF);
        if (bad >= 0) throw new MidiFormatException($"音色 {bad / VoiceLen + 1} のデータが壊れています（0x{v[bad]:X2}）");
        var m = new List<byte>(VmemLen + 8) { 0xF0, 0x43, (byte)(ch & 0x0F), 0x09, 0x20, 0x00 };
        m.AddRange(v.ToArray());
        int sum = 0;
        foreach (var b in v) sum += b;
        m.Add((byte)((0x80 - (sum & 0x7F)) & 0x7F));
        m.Add(0xF7);
        return m.ToArray();
    }
}
