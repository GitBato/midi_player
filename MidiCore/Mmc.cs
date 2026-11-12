namespace MidiCore;

/// <summary>
/// マイクロミュージシャン（初代）の曲ファイル（.MMC）を読み込む。
/// 公開された仕様は無いので、手元の曲ファイルから読み取った形式で読む（Rust 版 mmc.rs と同じ）。
/// </summary>
public static class Mmc
{
    const long Tpq = 48;
    const int Body = 0x200;

    /// <summary>区間の頭の印（FF FF FF F0〜FF）の位置を、from から探す</summary>
    static int? FindMark(byte[] d, int from)
    {
        for (int j = from; j < d.Length - 3; j++)
            if (d[j] == 0xFF && d[j + 1] == 0xFF && d[j + 2] == 0xFF && d[j + 3] >= 0xF0 && (j == 0 || d[j - 1] != 0xFF)) return j;
        return null;
    }

    public static bool IsMmc(byte[] d) =>
        d.Length > Body + 4
        && (Smf.StartsWith(d, 0, "MC") || (d[0] == 0 && d[1] == 0))
        && d[0x20] == 0x30
        && d[0x22] >= 1 && d[0x22] <= 16
        && d[0x23] != 0 && (d[0x23] & (d[0x23] - 1)) == 0 && d[0x23] <= 32
        && FindMark(d, Body) != null;

    static bool IsFE4(byte[] r) => r[0] == 0xFE && r[1] == 0xFE && r[2] == 0xFE && r[3] == 0xFE;

    /// <summary>繰り返し（|: 1 番 2 番かっこ、セーニョと D.S.）を展開する</summary>
    static (List<byte[]> outList, (int segnoBar, int dsBar)? ds) Expand(List<byte[]> evs)
    {
        var o = new List<byte[]>();
        int i = 0, repStart = 0, bars = 0, guard = 0;
        bool second = false, jumped = false;
        (int at, int bar)? segno = null;
        (int, int)? ds = null;
        void CloseBar()
        {
            if (o.Count > 0 && o[^1][0] != 0xFD)
            {
                o.Add(new byte[] { 0xFD, 0, 0, 0 });
                bars++;
            }
        }
        while (i < evs.Count && guard < 1_000_000)
        {
            guard++;
            var r = evs[i];
            i++;
            if (r[0] == 0xFC)
            {
                repStart = i;
                second = false;
            }
            else if (r[0] == 0xF0 && r[1] == 4)
            {
                // 2 回目は 1 番かっこを飛ばして 2 番かっこへ
                if (second)
                {
                    int j = evs.FindIndex(i, e => e[0] == 0xF0 && e[1] == 5);
                    if (j >= 0) i = j + 1;
                    second = false;
                    jumped = true;
                }
            }
            else if (r[0] == 0xF0 && r[1] == 5)
            {
                if (second) second = false;
                else
                {
                    CloseBar();
                    second = true;
                    i = repStart;
                    jumped = true;
                }
            }
            else if (r[0] == 0xF0 && r[1] == 1) segno = (i, bars);
            else if (r[0] == 0xF0 && r[1] == 0)
            {
                if (ds == null && segno is (int at, int bar))
                {
                    CloseBar();
                    ds = (bar, bars);
                    i = at;
                    second = false;
                    jumped = true;
                }
            }
            else if (r[0] == 0xF0) { }
            else
            {
                if (r[0] == 0xFD)
                {
                    if (jumped && (o.Count == 0 || o[^1][0] == 0xFD))
                    {
                        jumped = false;
                        continue;
                    }
                    bars++;
                }
                jumped = false;
                o.Add(r);
            }
        }
        return (o, ds);
    }

    public static Song Parse(byte[] d)
    {
        if (!IsMmc(d)) throw new MidiFormatException("マイクロミュージシャンの MMC ファイルではありません");
        byte num = d[0x22], den = d[0x23];
        long barLen = Tpq * 4 * num / den;
        uint baseBpm = Math.Max((uint)d[0x21], 10);

        // 区間の印
        var marks = new List<int>();
        int p = Body;
        while (FindMark(d, p) is int m)
        {
            marks.Add(m);
            p = m + 4;
            if (marks.Count == 5) break;
        }

        // トラックを読む。0〜7 が 0x200〜1 番目の印、8〜15 が 1 番目〜2 番目の印。どのトラックも FE×4 で終わる
        var tracks = new List<List<byte[]>>();
        var groups = new[] { (Body, marks[0]), (marks[0] + 4, marks.Count > 1 ? marks[1] : marks[0] + 4) };
        foreach (var (a, b) in groups)
        {
            var group = new List<List<byte[]>>();
            var cur = new List<byte[]>();
            for (int o = a; o + 4 <= b; o += 4)
            {
                var r = d.AsSpan(o, 4).ToArray();
                if (IsFE4(r))
                {
                    group.Add(cur);
                    cur = new List<byte[]>();
                }
                else cur.Add(r);
            }
            while (group.Count < 8) group.Add(new List<byte[]>());
            if (group.Count > 8) group.RemoveRange(8, group.Count - 8);
            tracks.AddRange(group);
        }

        // 小節 = (長さ, [(小節内の tick, 並び, データ)])。並び 0 = テンポ、1 = ノートオフ、2 = そのほか
        var parts = new List<List<(long len, List<(long t, int o, byte[] data)> list)>>();
        (int segnoBar, int dsBar)? ds = null;
        for (int ti = 0; ti < Math.Min(tracks.Count, 16); ti++)
        {
            var src = tracks[ti];
            if (!src.Any(r => r[0] < 0x80 && r[2] > 0 && r[3] > 0)) continue;
            byte ch = (byte)(d[0x40 + ti] & 0x0F);
            var (evs, thisDs) = Expand(src);
            ds ??= thisDs;
            var bars = new List<(long len, List<(long, int, byte[])> list)> { (0, new()) };
            foreach (var r in evs)
            {
                var (at, list) = bars[^1];
                if (r[0] < 0x80)
                {
                    if (r[2] > 0 && r[3] > 0)
                    {
                        list.Add((at, 2, new byte[] { (byte)(0x90 | ch), r[0], (byte)(r[3] & 0x7F) }));
                        list.Add((at + r[2], 1, new byte[] { (byte)(0x80 | ch), r[0], 0 }));
                    }
                }
                else
                {
                    switch (r[0])
                    {
                        case 0xE0: list.Add((at, 2, new byte[] { (byte)(0xC0 | ch), (byte)(r[2] & 0x7F) })); break;
                        case 0xE2: list.Add((at, 2, new byte[] { (byte)(0xB0 | ch), (byte)(r[2] & 0x7F), (byte)(r[3] & 0x7F) })); break;
                        case 0xE4: list.Add((at, 2, new byte[] { (byte)(0xE0 | ch), (byte)(r[2] & 0x7F), (byte)(r[3] & 0x7F) })); break;
                        case 0xFA: list.Add((at, 0, new byte[] { r[2] })); break;
                        case 0xFD: bars.Add((0, new())); continue;
                    }
                    if (r[0] is 0xF0 or 0xF2 or 0xFB or 0xFC) continue;
                }
                var last = bars[^1];
                bars[^1] = (last.len + r[1], last.list);
            }
            parts.Add(bars);
        }

        // 小節の長さは、多くのトラックで共通の長さにそろえる（同数なら長いほう）
        int nbars = parts.Count == 0 ? 0 : parts.Max(b => b.Count);
        var barStart = new long[nbars + 1];
        for (int k = 0; k < nbars; k++)
        {
            var lens = parts.Where(b => k < b.Count).Select(b => b[k].len).OrderBy(x => x).ToList();
            (int c, long l) best = (0, 0);
            foreach (var l in lens)
            {
                int c = lens.Count(x => x == l);
                if (c >= best.c) best = (c, l);
            }
            barStart[k + 1] = barStart[k] + best.l;
        }
        var all = new List<(long t, int o, byte[] data)>();
        foreach (var bars in parts)
            for (int k = 0; k < bars.Count; k++)
                all.AddRange(bars[k].list.Select(e => (barStart[k] + e.t, e.o, e.data)));
        long endTracks = barStart[nbars];

        // リズム
        var rhythm = new List<(long t, int o, byte[] data)>();
        long rhythmEnd = 0;
        if (marks.Count >= 5)
        {
            var drumMap = d.AsSpan(marks[2] + 4, Math.Max(Math.Min(marks[2] + 36, marks[3]) - (marks[2] + 4), 0)).ToArray();
            var velTable = d.AsSpan(marks[3] - 8, 8).ToArray();
            // パターン：(長さ, [(位置, 音程, ベロシティ)])
            var pats = new List<(long? len, List<(long at, byte key, byte vel)> hits)> { (null, new()) };
            for (int o = marks[3] + 4; o + 2 <= marks[4]; o += 2)
            {
                byte w0 = d[o], w1 = d[o + 1];
                var pat = pats[^1];
                if (w0 == 0xFE && w1 == 0xFE) pats.Add((null, new()));
                else if (w1 == 0) { if (pat.len == null) pats[^1] = (w0, pat.hits); }
                else if (pat.len == null)
                {
                    int ki = w1 & 0x1F;
                    byte key = ki < drumMap.Length ? drumMap[ki] : (byte)0;
                    if (key > 0 && key < 0x80) pat.hits.Add((w0, key, (byte)(velTable[w1 >> 5] & 0x7F)));
                }
            }
            long t = 0;
            for (int o = marks[1] + 4; o + 4 <= marks[2]; o += 4)
            {
                if (d[o] == 0xFE && d[o + 1] == 0xFE && d[o + 2] == 0xFE && d[o + 3] == 0xFE) break;
                long len = barLen;
                IEnumerable<(long at, byte key, byte vel)> hits = Array.Empty<(long, byte, byte)>();
                int pi = d[o] - 1;
                if (pi >= 0 && pi < pats.Count)
                {
                    len = pats[pi].len ?? barLen;
                    hits = pats[pi].hits;
                }
                foreach (var (at, key, vel) in hits.Where(h => h.at < len))
                {
                    rhythm.Add((t + at, 2, new byte[] { 0x99, key, vel }));
                    rhythm.Add((t + at + 1, 1, new byte[] { 0x89, key, 0 }));
                }
                t += len;
            }
            rhythmEnd = t;
        }
        // D.S. で戻る前にリズムの並びが終わっている曲は、戻った先（セーニョ）からのリズムをもう一度鳴らす
        if (ds is (int segnoBar, int dsBar))
        {
            long from = barStart[Math.Min(segnoBar, nbars)], to = barStart[Math.Min(dsBar, nbars)];
            if (rhythmEnd > from && rhythmEnd <= to)
            {
                var again = rhythm.Where(e => e.t >= from).Select(e => (e.t - from + to, e.o, (byte[])e.data.Clone())).ToList();
                rhythmEnd += to - from;
                rhythm.AddRange(again);
            }
        }
        all.AddRange(rhythm);
        if (all.All(e => e.o == 0)) throw new MidiFormatException("MMC に演奏データがありません");
        long endTick = Math.Max(endTracks, rhythmEnd);

        // tick → μs（FA のテンポ変更は、ヘッダのテンポに 値/64 を掛ける）
        all = all.OrderBy(e => e.t).ThenBy(e => e.o).ToList();
        uint UsPerQ(byte pct) => (uint)(60_000_000.0 * 64.0 / (baseBpm * (double)Math.Max(pct, (byte)1)));
        uint tempo = UsPerQ(64);
        long lastTick = 0;
        double nowUs = 0;
        var song = new Song { FirstBpm = baseBpm };
        foreach (var (t, kind, data) in all)
        {
            nowUs += (double)(t - lastTick) * tempo / Tpq;
            lastTick = t;
            if (kind == 0) tempo = UsPerQ(data[0]);
            else song.Events.Add(new TimedEvent((long)Math.Round(nowUs, MidpointRounding.AwayFromZero), data));
        }
        song.LengthUs = (long)Math.Round(nowUs + Math.Max(endTick - lastTick, 0) * (double)tempo / Tpq, MidpointRounding.AwayFromZero);
        return song;
    }
}
