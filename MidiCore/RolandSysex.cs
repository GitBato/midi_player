namespace MidiCore;

/// <summary>Roland の SysEx（DT1・チェックサム・7bit アドレス）を組み立てる共通の部品</summary>
public static class RolandSysex
{
    /// <summary>Roland チェックサム：アドレスとデータの合計を 128 で割った余りを、128 から引いたもの</summary>
    public static byte Checksum(IEnumerable<byte> body) => (byte)((128 - body.Sum(b => b) % 128) % 128);

    public static int AddrLin((byte, byte, byte) a) => a.Item1 << 14 | a.Item2 << 7 | a.Item3;

    public static (byte, byte, byte) AddrAdd((byte, byte, byte) b, int off)
    {
        int lin = AddrLin(b) + off;
        return ((byte)((lin >> 14) & 0x7F), (byte)((lin >> 7) & 0x7F), (byte)(lin & 0x7F));
    }

    /// <summary>Roland の DT1（F0 41 dev model 12 addr… data… sum F7）</summary>
    public static byte[] Dt1(byte dev, byte model, (byte, byte, byte) addr, ReadOnlySpan<byte> data)
    {
        var v = new List<byte>(data.Length + 10) { 0xF0, 0x41, dev, model, 0x12, addr.Item1, addr.Item2, addr.Item3 };
        v.AddRange(data.ToArray());
        v.Add(Checksum(v.Skip(5)));
        v.Add(0xF7);
        return v.ToArray();
    }

    public static byte[] Mt32Dt1((byte, byte, byte) addr, ReadOnlySpan<byte> data) => Dt1(0x10, 0x16, addr, data);

    /// <summary>MT-32 の音色 1 つ（246 バイト）を、Common 14 ＋ パーシャル 58 × 4 の 5 つの DT1 に分けて積む</summary>
    public static void PushMt32Timbre(List<byte[]> o, (byte, byte, byte) b, byte[] t)
    {
        o.Add(Mt32Dt1(b, t.AsSpan(0, 14)));
        for (int j = 0; j < 4; j++)
        {
            int off = 14 + 58 * j;
            o.Add(Mt32Dt1(AddrAdd(b, off), t.AsSpan(off, 58)));
        }
    }

    /// <summary>MT-32 本体の液晶に 20 文字を表示する DT1</summary>
    public static byte[] Mt32Display(string text)
    {
        var b = System.Text.Encoding.ASCII.GetBytes(text).Take(20).ToList();
        while (b.Count < 20) b.Add((byte)' ');
        return Mt32Dt1((0x20, 0x00, 0x00), b.ToArray());
    }
}
