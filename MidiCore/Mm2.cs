namespace MidiCore;

/// <summary>
/// マイクロミュージシャン Ver 2.00 の曲ファイル（.MM2、"MicroMusicianVer2.00SNG"）を読み込む。
/// 公開された仕様は無いので、手元の曲ファイルから読み取った形式で読む（Rust 版 mm2.rs と同じ）。
/// </summary>
public static class Mm2
{
    const long Tpq = 48;
    const int Data = 0xD40;
    const int Tracks = 16;

    public static bool IsMm2(byte[] d) =>
        d.Length >= Data && Smf.StartsWith(d, 0, "MicroMusicianVer2") && Smf.StartsWith(d, 0x14, "SNG ");

    /// <summary>区間 i（0〜17）の範囲</summary>
    static (int a, int b) Section(byte[] d, int i)
    {
        int End(int k) => Data + 16 * (d[0x200 + 2 * k] | d[0x201 + 2 * k] << 8);
        int a = i == 0 ? Data : End(i - 1);
        return (Math.Min(a, d.Length), Math.Min(End(i), d.Length));
    }

    public static Song Parse(byte[] d)
    {
        if (!IsMm2(d)) throw new MidiFormatException("マイクロミュージシャンの MM2 ファイルではありません");
        int num = Math.Clamp((int)d[0x194], 1, 32);
        int den = d[0x195] != 0 && (d[0x195] & (d[0x195] - 1)) == 0 ? d[0x195] : 4;
        long bar = Tpq * 4 * num / den;
        uint baseBpm = Math.Max((uint)d[0x196], 10);

        // (tick, 並び, データ)。並び 0 = テンポ（データは 64 を元とする倍率）、1 = ノートオフ、2 = そのほか
        var evs = new List<(long t, int kind, byte[] data)>();
        long endTick = 0;
        for (int ti = 0; ti < Tracks; ti++)
        {
            var (a, b) = Section(d, ti);
            byte ch = (byte)(d[0x1A9 + ti] & 0x0F);
            long t = 0;
            int o = a;
            bool any = false;
            int first = evs.Count;
            while (o + 4 <= b)
            {
                byte r0 = d[o], r1 = d[o + 1], r2 = d[o + 2], r3 = d[o + 3];
                if (r0 < 0x80)
                {
                    // 音符・休符（8 バイト）
                    byte key = r0;
                    long step = r1, gate = r2;
                    byte vel = (byte)(r3 & 0x7F);
                    if (key > 0 && gate > 0 && vel > 0)
                    {
                        evs.Add((t, 2, new byte[] { (byte)(0x90 | ch), key, vel }));
                        evs.Add((t + gate, 1, new byte[] { (byte)(0x80 | ch), key, 0 }));
                        any = true;
                    }
                    t += step;
                    o += 8;
                    continue;
                }
                if (r0 == 0xFF && r1 == 0xFC) break;
                switch (r0)
                {
                    case 0xB0 or 0xE0:
                        evs.Add((t, 2, new byte[] { (byte)(r0 | ch), (byte)(r2 & 0x7F), (byte)(r3 & 0x7F) }));
                        any = true;
                        break;
                    case 0xC0:
                        evs.Add((t, 2, new byte[] { (byte)(0xC0 | ch), (byte)(r2 & 0x7F) }));
                        any = true;
                        break;
                    case 0xF0:
                        evs.Add((t, 0, new byte[] { r2 }));
                        break;
                }
                if (r0 != 0xFF) t += r1;
                o += 4;
            }
            if (any)
            {
                // 初期の音量とパン（このトラックの先頭に入れる）
                evs.InsertRange(first, new[]
                {
                    (0L, 2, new byte[] { (byte)(0xB0 | ch), 7, (byte)(d[0x1BA + ti] & 0x7F) }),
                    (0L, 2, new byte[] { (byte)(0xB0 | ch), 10, (byte)(d[0x1ED + ti] & 0x7F) }),
                });
                endTick = Math.Max(endTick, t);
            }
        }

        // リズム：区間 17 のパターンを、区間 16 の並びどおりに 1 小節ずつ鳴らす
        var patterns = RhythmPatterns(d);
        {
            var (a, b) = Section(d, 16);
            byte ch = (byte)(d[0x1A9 + 16] & 0x0F);
            long barStart = 0;
            int o = a;
            while (o + 4 <= b && !(d[o] == 0xFF && d[o + 1] == 0xFC))
            {
                if (d[o] < patterns.Count)
                {
                    foreach (var (at, key, level) in patterns[d[o]].Where(h => h.at < bar))
                    {
                        // 強さ 1〜8 を、0xD32〜 の表でベロシティにする
                        int vi = 0xD31 + Math.Clamp((int)level, 1, 8);
                        byte vel = (byte)((vi < d.Length ? d[vi] : 100) & 0x7F);
                        evs.Add((barStart + at, 2, new byte[] { (byte)(0x90 | ch), key, vel }));
                        evs.Add((barStart + at + 1, 1, new byte[] { (byte)(0x80 | ch), key, 0 }));
                    }
                }
                barStart += bar;
                o += 4;
            }
            endTick = Math.Max(endTick, barStart);
        }
        if (evs.All(e => e.kind == 0)) throw new MidiFormatException("MM2 に演奏データがありません");

        // tick → μs（F0 のテンポ変更は、ヘッダのテンポに 値/64 を掛ける）
        evs = evs.OrderBy(e => e.t).ThenBy(e => e.kind).ToList();
        uint UsPerQ(byte pct) => (uint)(60_000_000.0 * 64.0 / (baseBpm * (double)Math.Max(pct, (byte)1)));
        uint tempo = UsPerQ(64);
        long lastTick = 0;
        double nowUs = 0;
        var song = new Song { FirstBpm = baseBpm };
        foreach (var (t, kind, data) in evs)
        {
            nowUs += (double)(t - lastTick) * tempo / Tpq;
            lastTick = t;
            if (kind == 0) tempo = UsPerQ(data[0]);
            else song.Events.Add(new TimedEvent((long)Math.Round(nowUs, MidpointRounding.AwayFromZero), data));
        }
        song.LengthUs = (long)Math.Round(nowUs + Math.Max(endTick - lastTick, 0) * (double)tempo / Tpq, MidpointRounding.AwayFromZero);
        return song;
    }

    /// <summary>区間 17 のリズムパターン。打撃ごとに（小節頭からの tick, 音程, 強さ）を返す</summary>
    static List<List<(long at, byte key, byte level)>> RhythmPatterns(byte[] d)
    {
        var (a, b) = Section(d, 17);
        var pats = new List<List<(long, byte, byte)>>();
        long t = 0;
        for (int o = a; o + 4 <= b; o += 4)
        {
            byte r0 = d[o], r1 = d[o + 1], r3 = d[o + 3];
            if (r0 == 0xFF && r1 == 0xF9)
            {
                pats.Add(new());
                t = 0;
            }
            else if (r0 == 0xFF && r1 == 0xFC) break;
            else if (r0 == 0) t += r1;
            else if (r0 < 0x80) pats.LastOrDefault()?.Add((t, r0, r3));
        }
        return pats;
    }
}
