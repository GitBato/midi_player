namespace MidiCore;

/// <summary>MIDI OUT の出口の種類</summary>
public enum Port
{
    /// <summary>GS（SC-88 など）・GM の音源</summary>
    Main,
    Xg,
    Mt32,
    /// <summary>その他の音源（TX7 など）</summary>
    Other,
}

/// <summary>メッセージの送り先と、送ったあとの待ち時間を決める</summary>
public static class Routing
{
    public static readonly byte[] Mt32Reset = { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x00, 0x01, 0xF7 };

    // 音源の種類（大きいほど優先。MT-32 < GM < XG < GS < SC-88）
    public const int SsMt32 = 0, SsGm = 1, SsXg = 2, SsGs = 3, SsSc88 = 4;

    static bool Starts(byte[] d, params byte[] p) => d.Length >= p.Length && d.AsSpan(0, p.Length).SequenceEqual(p);

    /// <summary>リセット系の SysEx なら、その音源の種類</summary>
    public static int? SoundSetOf(byte[] d)
    {
        if (Starts(d, 0xF0, 0x41, 0x10, 0x42, 0x12, 0x00, 0x00, 0x7F)) return SsSc88; // SC-88 System Mode Set
        if (Starts(d, 0xF0, 0x41, 0x10, 0x42, 0x12, 0x40, 0x00, 0x7F)) return SsGs;   // GS Reset
        if (Starts(d, 0xF0, 0x43, 0x10, 0x4C, 0x00, 0x00, 0x7E)) return SsXg;         // XG System On
        if (Starts(d, 0xF0, 0x7E, 0x7F, 0x09, 0x01) || Starts(d, 0xF0, 0x7E, 0x7F, 0x09, 0x03)) return SsGm; // GM / GM2 System On
        return null;
    }

    public static bool IsReset(byte[] d) => SoundSetOf(d) != null;

    /// <summary>曲の種類から決める送り先（XG System On → XG、GS/GM → GS、リセットが無ければ MT-32 向け）</summary>
    public static Port SongKind(Song song)
    {
        int ss = SsMt32;
        foreach (var e in song.Events)
            if (SoundSetOf(e.Data) is int s && s > ss) ss = s;
        return ss switch { SsMt32 => Port.Mt32, SsXg => Port.Xg, _ => Port.Main };
    }

    /// <summary>
    /// メッセージ 1 つの送り先。SysEx の宛先 → トラックの送り先 → 曲の送り先 の順に決める。
    /// Roland の MT-32 宛て → MT-32、GS 宛て → GS、Yamaha の XG・MU 宛て → XG、それ以外の Yamaha（TX7 など）→ その他、
    /// ユニバーサル SysEx → 曲が XG なら XG、それ以外は GS
    /// </summary>
    public static Port PortFor(byte[] msg, Dest dest, Port songPort)
    {
        if (msg.Length > 0 && msg[0] == 0xF0 && msg.Length > 1)
        {
            byte maker = msg[1];
            byte model = msg.Length > 3 ? msg[3] : (byte)0;
            if (maker == 0x41 && msg.Length > 3)
            {
                if (model == 0x16) return Port.Mt32;
                if (model is 0x42 or 0x45) return Port.Main;
            }
            if (maker == 0x43)
                return msg.Length > 3 && model is 0x4C or 0x49 ? Port.Xg : Port.Other;
            if (maker is 0x7E or 0x7F) return songPort == Port.Xg ? Port.Xg : Port.Main;
        }
        return dest switch
        {
            Dest.Main => Port.Main,
            Dest.Mt32 => Port.Mt32,
            Dest.Xg => Port.Xg,
            Dest.Other => Port.Other,
            _ => songPort,
        };
    }

    /// <summary>
    /// SysEx を port の音源へ送った後に待つ時間（ms）。リセットや大きい SysEx（15 バイト以上）はどの音源でも 50ms、
    /// 短い SysEx は MT-32 宛てだけ待つ（初期型 MT-32 は処理が追いつかない）
    /// </summary>
    public static int SysexWaitMs(byte[] msg, Port port, int mt32ShortWaitMs = 10)
    {
        if (msg.Length == 0 || msg[0] != 0xF0) return 0;
        bool rolandReset = msg.Length > 5 && msg[1] == 0x41 && msg[4] == 0x12 && msg[5] == 0x7F;
        if (msg.Length > 14 || IsReset(msg) || rolandReset) return 50;
        return port == Port.Mt32 ? Math.Clamp(mt32ShortWaitMs, 1, 50) : 0;
    }
}
