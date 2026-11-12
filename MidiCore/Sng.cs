namespace MidiCore;

/// <summary>
/// Dynaware / Roland(BOSS) Ballade の SNG ファイルを読み込む。"BALLADE SONG Ver. 1.00/2.00/3.00/4.00" 形式に対応。
/// v2.00 以降の変換手順は、MIT ライセンスの jmplayer sngmidi.cpp（および glosa SngConverter.cs）を参考にした独自実装。
/// </summary>
public static class Sng
{
    const int Tpq = 48;

    public static bool IsSng(byte[] d) => d.Length >= 32 && Smf.StartsWith(d, 0, "BALLADE SONG");

    static int U16(byte[] d, int o) => (o >= 0 && o < d.Length ? d[o] : 0) | (o + 1 >= 0 && o + 1 < d.Length ? d[o + 1] : 0) << 8;
    static long U32(byte[] d, int o) => (uint)U16(d, o) | (long)(uint)U16(d, o + 2) << 16;

    sealed class Layout
    {
        public bool V1;
        public int Parts;
        public int[] Starts = Array.Empty<int>();
        public int[] Sizes = Array.Empty<int>();
    }

    static Layout? TryLayout(byte[] d)
    {
        if (d.Length < 0x100) return null;
        string version = System.Text.Encoding.ASCII.GetString(d, 18, 4);
        if (version == "1.00") return TryLayoutV1(d);
        int parts = version switch { "2.00" or "3.00" => 10, "4.00" => 16, _ => 0 };
        if (parts == 0) return null;
        int tableEnd = 0x46 + 4 * (parts + 1);
        if (tableEnd + 8 > d.Length) return null;
        var entries = Enumerable.Range(0, parts + 1).Select(i => U32(d, 0x46 + 4 * i)).ToArray();

        // ヘッダ末尾（コンダクタ開始位置）の候補を順に試す。v2.00 は table_end + 2 を先に試す
        int[] candidates = version == "2.00" ? new[] { tableEnd + 2, tableEnd + 94, tableEnd } : new[] { tableEnd + 94, tableEnd + 2, tableEnd };
        foreach (int headerEnd in candidates)
        {
            if (headerEnd == 0 || headerEnd >= d.Length) continue;
            var starts = new int[parts + 1];
            var sizes = new int[parts + 1];
            long offset = headerEnd;
            starts[0] = (int)offset;
            sizes[0] = (int)Math.Min(entries[0], int.MaxValue);
            offset += entries[0];
            bool ok = true;
            for (int p = 1; p <= parts; p++)
            {
                long size = entries[p] + 81;
                if (offset < 0 || offset + 158 > d.Length || size < 158 || size > int.MaxValue) { ok = false; break; }
                starts[p] = (int)offset;
                sizes[p] = (int)size;
                int used = U16(d, (int)offset + 112);
                int ctl = U16(d, (int)offset + 114);
                int idx = U16(d, (int)offset + 116);
                long capacity = size - 158;
                if (used % 8 != 0 || ctl % 4 != 0 || (long)used + ctl + idx > capacity) { ok = false; break; }
                offset += size;
            }
            if (ok) return new Layout { V1 = false, Parts = parts, Starts = starts, Sizes = sizes };
        }
        return null;
    }

    /// <summary>v1.00：0x46 からのサイズ表どおりに、0xB5 からコンダクタ、続いて各パート（サイズ＋80 バイト）</summary>
    static Layout? TryLayoutV1(byte[] d)
    {
        const int Parts = 10, Cond = 0xB5;
        if (d.Length < Cond) return null;
        var entries = Enumerable.Range(0, Parts + 1).Select(i => (int)Math.Clamp(U32(d, 0x46 + 4 * i), 0, int.MaxValue / 2)).ToArray();
        var starts = new List<int> { Cond };
        var sizes = new List<int> { entries[0] };
        long offset = Cond + (long)entries[0];
        foreach (int size in entries.Skip(1))
        {
            // パートの頭にも自分のサイズが入っている
            if (offset + 80 > d.Length || U32(d, (int)offset) != size) return null;
            starts.Add((int)offset);
            sizes.Add(size + 80);
            offset += size + 80;
        }
        return new Layout { V1 = true, Parts = Parts, Starts = starts.ToArray(), Sizes = sizes.ToArray() };
    }

    /// <summary>コンダクタトラックからテンポ変化（tick, μs/四分音符）を読む。実BPM = base_bpm × value / 64</summary>
    static List<(long tick, uint us)> ReadTempi(byte[] d, int start, int end, int baseBpm, bool v1)
    {
        var result = new List<(long, uint)>();
        byte[] sentinel = { 0xFF, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF };
        long last = -1;
        int skip = v1 ? 10 : 12;
        for (int i = start; i + 6 <= end; i++)
            if (d.AsSpan(i, 6).SequenceEqual(sentinel)) last = i;
        if (last < 0) return result;
        int at = (int)last + skip;
        long tick = 0;
        int previous = -1;
        while (at + 4 <= end)
        {
            int step = U16(d, at);
            int value = U16(d, at + 2);
            if (value <= 0 || value > 500 || step > 4000) break;
            int bpm = Math.Clamp(baseBpm * value / 64, 1, 600);
            int us = 60_000_000 / bpm;
            if (us != previous)
            {
                result.Add((tick, (uint)us));
                previous = us;
            }
            tick += step;
            at += 4;
        }
        return result;
    }

    /// <summary>コンダクタの小節情報の列から、最初の小節長を読む（v2 以降 12 バイト、v1 10 バイトずつ）</summary>
    static int? FirstBarLength(byte[] d, int start, int end, int stride)
    {
        if (start + stride > end) return null;
        int len = U16(d, start);
        byte num = d[start + 2], den = d[start + 3];
        bool valid = num >= 1 && num <= 32 && (den is 1 or 2 or 4 or 8 or 16 or 32) && len == num * (Tpq * 4 / den);
        return valid ? len : null;
    }

    public static Song Parse(byte[] d)
    {
        if (!IsSng(d)) throw new MidiFormatException("Ballade SNG 形式ではありません");
        var layout = TryLayout(d) ?? throw new MidiFormatException("対応していない Ballade SNG レイアウトです");
        int parts = layout.Parts;
        bool v1 = layout.V1;

        int condStart = layout.Starts[0];
        int condEnd = (int)Math.Min(d.Length, (long)condStart + layout.Sizes[0]);
        int barLength = (v1 ? FirstBarLength(d, condStart, condEnd, 10) : FirstBarLength(d, condStart + 77, condEnd, 12)) ?? 192;

        // 基準テンポ（BPM）はコンダクタ先頭 +5。テンポ列はこの値に対する百分率(64=100%)
        int baseBpm = U16(d, v1 ? 0x8F : condStart + 5);
        if (baseBpm < 20 || baseBpm > 600) baseBpm = 120;
        var tempi = ReadTempi(d, condStart, condEnd, baseBpm, v1);

        int settings = 0x46 + 4 * (parts + 1);
        int transpose = !v1 && settings + 11 <= d.Length ? (short)U16(d, settings + 9) : 0;

        // リードイン（"2.00" は 128、v1 は無し、それ以外は小節長）
        string version = System.Text.Encoding.ASCII.GetString(d, 18, 4);
        int partOffset = version switch { "2.00" => 128, "1.00" => 0, _ => barLength };

        var evs = new List<(long tick, uint order, byte[] data)>();
        uint orderSeq = 1;

        for (int part = 1; part <= parts; part++)
        {
            int start = layout.Starts[part];
            int end = (int)Math.Min(d.Length, (long)start + layout.Sizes[part]);
            if (start + 158 > d.Length) break;
            // パートのヘッダ：v1 は 80 バイト（音符・コントロールのバイト数は +0x32／+0x34）、v2 以降は 158 バイト
            int used, ctlBytes, stream;
            if (v1)
            {
                used = U16(d, start + 0x32);
                ctlBytes = U16(d, start + 0x34);
                stream = start + 80;
            }
            else
            {
                used = U16(d, start + 112);
                ctlBytes = U16(d, start + 114);
                stream = start + 158;
            }
            int controllers = stream + used;
            byte channel = (byte)(part - 1);
            byte module = start + 94 < d.Length ? d[start + 94] : (byte)0;
            int shift = module is 2 or 5 ? 0 : transpose;

            // ノートストリーム（8 バイト固定）
            int safeUsed = Math.Min(used, Math.Max(end - stream, 0));
            long tick = 0;
            // 音高 → [(note-off tick, evs内index)]
            var pending = new Dictionary<byte, List<(long off, int evi)>>();
            for (int pos = 0; pos + 8 <= safeUsed; pos += 8)
            {
                int r = stream + pos;
                if (d[r] == 0xFC) break;
                long step = d[r + 2];
                long nudge = (sbyte)d[r + 3];
                long duration = d[r + 1];
                if (d[r + 1] != 0)
                {
                    long at = Math.Max(tick + nudge, 0);
                    byte key = (byte)Math.Clamp(d[r] + shift, 0, 127);
                    if ((d[r + 7] & 0x80) != 0)
                    {
                        // タイ：直近の同音 note-off を延長する
                        if (pending.TryGetValue(key, out var cands))
                        {
                            int ci = -1;
                            long ctick = long.MinValue;
                            int cidxEvi = 0;
                            for (int i = 0; i < cands.Count; i++)
                            {
                                var (t, evi) = cands[i];
                                if (t <= at && (t > ctick || (t == ctick && evi > cidxEvi)))
                                {
                                    ci = i;
                                    ctick = t;
                                    cidxEvi = evi;
                                }
                            }
                            if (ci >= 0)
                            {
                                int evi = cands[ci].evi;
                                cands.RemoveAt(ci);
                                long newOff = at + duration;
                                evs[evi] = (newOff, evs[evi].order, evs[evi].data);
                                cands.Add((newOff, evi));
                            }
                        }
                    }
                    else
                    {
                        evs.Add((at, orderSeq++, new byte[] { (byte)(0x90 | channel), key, d[r + 4] }));
                        evs.Add((at + duration, orderSeq++, new byte[] { (byte)(0x90 | channel), key, 0 }));
                        if (!pending.TryGetValue(key, out var list)) pending[key] = list = new();
                        list.Add((at + duration, evs.Count - 1));
                    }
                }
                tick += step;
            }

            // コントローラストリーム（4 バイト固定）
            int safeCtl = Math.Min(ctlBytes, Math.Max(end - controllers, 0));
            long ctick2 = 0;
            for (int cpos = 0; cpos + 4 <= safeCtl; cpos += 4)
            {
                int r = controllers + cpos;
                if (d[r] == 0xFC) break;
                int status = d[r] & 0xF0;
                byte d1 = (byte)(d[r + 2] & 0x7F);
                byte d2 = (byte)(d[r + 3] & 0x7F);
                if (status is 0xB0 or 0xE0) evs.Add((ctick2, orderSeq++, new byte[] { (byte)(status | channel), d1, d2 }));
                else if (status == 0xC0) evs.Add((ctick2, orderSeq++, new byte[] { (byte)(0xC0 | channel), d1 }));
                ctick2 += d[r + 1];
            }

            // ピッチベンドレンジ ±2（RPN）を頭に入れておく
            evs.Add((0, 0, new byte[] { (byte)(0xB0 | channel), 101, 0 }));
            evs.Add((0, 0, new byte[] { (byte)(0xB0 | channel), 100, 0 }));
            evs.Add((0, 0, new byte[] { (byte)(0xB0 | channel), 6, 2 }));
        }

        if (evs.Count == 0) throw new MidiFormatException("Ballade SNG に読めるパートがありません");

        // テンポマップ（初期テンポ ＋ 各ポイントは小節長ぶんずらす）
        uint firstTempo = tempi.Count > 0 ? tempi[0].us : (uint)(60_000_000 / baseBpm);
        var tempoMap = new List<(long t, uint us)> { (0, firstTempo) };
        long previous = 0;
        foreach (var (ttick, us) in tempi)
        {
            long at = ttick + (v1 ? 0 : barLength);
            if (at <= previous && ttick == 0) continue;
            tempoMap.Add((Math.Max(at, 0), us));
            previous = at;
        }

        // すべてを (tick, order) でソートして μs に変換
        var merged = new List<(long t, uint o, bool isTempo, uint us, byte[]? data)>();
        foreach (var (t, us) in tempoMap) merged.Add((t, 0, true, us, null));
        foreach (var e in evs) merged.Add((Math.Max(e.tick + partOffset, 0), Math.Max(e.order, 1), false, 0, e.data));
        merged = merged.OrderBy(m => m.t).ThenBy(m => m.o).ToList();

        uint tempo = firstTempo;
        long lastTick = 0;
        double lastUs = 0;
        var song = new Song();
        foreach (var m in merged)
        {
            lastUs += (double)(m.t - lastTick) * tempo / Tpq;
            lastTick = m.t;
            long tus = (long)Math.Round(lastUs, MidpointRounding.AwayFromZero);
            song.LengthUs = Math.Max(song.LengthUs, tus);
            if (m.isTempo) tempo = m.us;
            else song.Events.Add(new TimedEvent(tus, m.data!, Dest.Song, m.data![0] & 0x0F)); // パート n はチャンネル n-1
        }

        // フッタのカスタム MT-32 音色を、読み込み時に送る SysEx 列として用意する
        int footer = layout.Starts[parts] + layout.Sizes[parts];
        var timbres = ParseTimbres(d, footer);
        int n = timbres.Count;
        // 1) 各音色を Timbre Memory #1..N へ書き込む（Common とパーシャルに分けて送る）
        for (int i = 0; i < n; i++) RolandSysex.PushMt32Timbre(song.TimbreSysex, (0x08, (byte)((i * 2) & 0x7F), 0x00), timbres[i]);
        // 2) Patch Memory #1..N を「Memory 音色 #0..N-1」を指すように書き換える
        for (int k = 0; k < n; k++)
        {
            byte[] data = { 2, (byte)(k & 0x3F), 24, 50, 12, 0, 1, 0 };
            song.TimbreSysex.Add(RolandSysex.Mt32Dt1((0x05, 0x00, (byte)((k * 8) & 0x7F)), data));
        }
        // 3) パーシャル・リザーブ（MT-32 System 10 00 04）を曲に合わせて送る
        var res = PartialReserve(song.Events, timbres);
        if (res != null) song.TimbreSysex.Add(RolandSysex.Mt32Dt1((0x10, 0x00, 0x04), res));
        // 4) 確認用：MT-32 本体の液晶に「Tone Set N」を表示する
        if (n > 0) song.TimbreSysex.Add(RolandSysex.Mt32Display($"Tone Set {n}"));

        song.FirstBpm = 60_000_000.0 / firstTempo;
        song.TrackNames = Enumerable.Range(1, parts).Select(p => $"パート {p}").ToList();
        return song;
    }

    const int TimbreBytes = 246; // MT-32 音色 = Common 14 + Partial×4×58

    static bool ValidTimbre(ReadOnlySpan<byte> b) =>
        b.Length == TimbreBytes && !b.ContainsAnyInRange((byte)0x80, (byte)0xFF)
        && b[10] <= 12 && b[11] <= 12 && b[12] <= 15 && b[13] <= 3 && b[14] <= 96 && b[15] <= 100;

    /// <summary>フッタ末尾にパックされた MT-32 カスタム音色（各 246 バイト）を取り出す</summary>
    static List<byte[]> ParseTimbres(byte[] d, int footer)
    {
        int n = d.Length;
        int bestCnt = 0, bestEnd = 0;
        for (int t = 0; t < 400 && t < n; t++)
        {
            int end = n - t;
            int cnt = 0;
            while (true)
            {
                if (end < (cnt + 1) * TimbreBytes) break;
                int s = end - (cnt + 1) * TimbreBytes;
                if (s < footer || !ValidTimbre(d.AsSpan(s, TimbreBytes))) break;
                cnt++;
            }
            if (cnt > bestCnt)
            {
                bestCnt = cnt;
                bestEnd = end;
            }
        }
        var o = new List<byte[]>();
        for (int k = 0; k < bestCnt; k++)
        {
            int s = bestEnd - (bestCnt - k) * TimbreBytes;
            o.Add(d.AsSpan(s, TimbreBytes).ToArray());
        }
        return o;
    }

    /// <summary>曲の発音状況から MT-32 のパーシャル・リザーブ（9 バイト、合計 32 以下）を決める</summary>
    static byte[]? PartialReserve(List<TimedEvent> events, List<byte[]> timbres)
    {
        var active = new int[16];
        var maxpoly = new int[16];
        var lastPc = new int?[16];
        foreach (var e in events)
        {
            var d = e.Data;
            if (d.Length == 0 || d[0] >= 0xF0) continue;
            int ch = d[0] & 0x0F;
            switch (d[0] & 0xF0)
            {
                case 0x90 when d.Length >= 3 && d[2] > 0:
                    active[ch]++;
                    maxpoly[ch] = Math.Max(maxpoly[ch], active[ch]);
                    break;
                case 0x90 or 0x80:
                    active[ch] = Math.Max(active[ch] - 1, 0);
                    break;
                case 0xC0 when d.Length >= 2:
                    lastPc[ch] = d[1];
                    break;
            }
        }
        // 1 音あたりのパーシャル数：メモリ音色ならミュートビットの数、プリセットは 2 と見なす
        int PerVoice(int ch)
        {
            if (ch == 9) return 1; // リズムは 1 打ほぼ 1 パーシャル
            if (lastPc[ch] is int pc && pc < timbres.Count)
                return Math.Max(System.Numerics.BitOperations.PopCount((uint)(timbres[pc][12] & 0x0F)), 1);
            return 2;
        }
        var need = new int[9];
        for (int i = 0; i < 9; i++)
        {
            int ch = i < 8 ? i + 1 : 9;
            need[i] = maxpoly[ch] * PerVoice(ch);
        }
        int total = need.Sum();
        if (total == 0) return null;
        var res = new int[9];
        if (total <= 32) res = need; // 余りは共有プールとして残す
        else
        {
            for (int i = 0; i < 9; i++) if (need[i] > 0) res[i] = Math.Max(need[i] * 32 / total, 1);
            // 合計を 32 に合わせる（多い所から削る／足りなければ必要数の大きい所へ足す）。同点は後ろを選ぶ
            while (res.Sum() > 32)
            {
                int best = -1;
                for (int i = 0; i < 9; i++) if (res[i] > 1 && (best < 0 || res[i] >= res[best])) best = i;
                res[best]--;
            }
            while (res.Sum() < 32)
            {
                int best = 0;
                for (int i = 0; i < 9; i++) if (need[i] - res[i] >= need[best] - res[best]) best = i;
                res[best]++;
            }
        }
        return res.Select(v => (byte)Math.Clamp(v, 0, 32)).ToArray();
    }
}
