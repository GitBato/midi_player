namespace MidiCore;

/// <summary>
/// レコンポーザ (Recomposer) ファイルの読み込み。
/// RCP / R36（V2: "RCM-PC98V2.0"）と G36（V3: "COME ON MUSIC … RCP3.0"）に対応する。
/// 変換ロジックは Valley Bell 版 rcp2mid（GPL-2.0）および公開フォーマット仕様を参考にした独自実装。
/// </summary>
public static class Rcp
{
    /// <summary>無限ループ（カウント 0 または 0x7F 以上）を何回展開するか</summary>
    const uint LoopInfiniteTimes = 1;

    public static bool IsRcp(byte[] b) => Smf.StartsWith(b, 0, "RCM-PC98V2.0") || Smf.StartsWith(b, 0, "COME ON MUSIC");

    /// <summary>ヘッダに書かれた CM6 ファイル名（書かれていなければ null）。V2 は 0x1C6、V3 は 0x298 から 16 バイト</summary>
    public static byte[]? Cm6Name(byte[] b)
    {
        int at = Smf.StartsWith(b, 0, "RCM-PC98V2.0") ? 0x1C6 : 0x298;
        if (at + 16 > b.Length) return null;
        var name = b.Skip(at).Take(16).TakeWhile(c => c != 0).ToList();
        while (name.Count > 0 && name[^1] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0C) name.RemoveAt(name.Count - 1);
        return name.Count > 0 ? name.ToArray() : null;
    }

    sealed class Raw
    {
        public long Tick;
        public int Seq;
        public bool IsTempo;
        public byte[] Data = Array.Empty<byte>();
        public uint Tempo;
        /// <summary>MIDI ポート（0 = A, 1 = B …）</summary>
        public byte Dev;
        /// <summary>トラックのコメントに書かれた音源名からの送り先</summary>
        public Dest? Named;
        public int Track;
    }

    static int Le16(byte[] d, int p) => (p < d.Length ? d[p] : 0) | (p + 1 < d.Length ? d[p + 1] : 0) << 8;
    static uint Le32(byte[] d, int p) => (uint)(Le16(d, p) | Le16(d, p + 2) << 16);
    static int Sign8(byte b) => (b & 0x80) != 0 ? b - 0x100 : b;
    static byte At(byte[] d, int p, byte def) => p >= 0 && p < d.Length ? d[p] : def;

    /// <summary>DX 系デバイス SysEx のパラメータ（C0〜CF、0xFF は無効）</summary>
    static readonly byte[] DxParam = { 0x08, 0x00, 0x04, 0x11, 0xFF, 0x15, 0xFF, 0x12, 0x13, 0x10, 0xFF, 0xFF, 0x1B, 0x18, 0x19, 0x1A };

    /// <summary>RCP の SysEx テンプレート（特殊バイト 80-84 を展開）を実バイト列へ。F0 は含まない</summary>
    internal static List<byte> ProcessRcpSysex(IReadOnlyList<byte> data, byte p1, byte p2, byte chn)
    {
        var o = new List<byte>();
        uint chk = 0;
        void Push(byte v)
        {
            if ((v & 0x80) == 0)
            {
                o.Add(v);
                chk = (chk + v) & 0xFF;
            }
        }
        foreach (var b in data)
        {
            if ((b & 0x80) != 0)
            {
                switch (b)
                {
                    case 0x80: Push(p1); break;
                    case 0x81: Push(p2); break;
                    case 0x82: Push(chn); break;
                    case 0x83: chk = 0; break;
                    case 0x84: Push((byte)((0x100u - (chk & 0xFF)) & 0x7F)); break;
                    case 0xF7: o.Add(0xF7); return o;
                }
            }
            else Push(b);
        }
        return o;
    }

    public static Song Parse(byte[] bytes)
    {
        int ver;
        if (Smf.StartsWith(bytes, 0, "RCM-PC98V2.0")) ver = 2;
        else if (Smf.StartsWith(bytes, 0, "COME ON MUSIC")) ver = 3;
        else throw new MidiFormatException("レコンポーザ形式ではありません");

        int tpqn, bpm, gblTransp, trkCount, usrBase, dataStart;
        if (ver == 2)
        {
            tpqn = At(bytes, 0x1C0, 48) | At(bytes, 0x1E7, 0) << 8;
            if (tpqn == 0) tpqn = 48;
            bpm = At(bytes, 0x1C1, 120);
            if (bpm == 0) bpm = 120;
            gblTransp = Sign8(At(bytes, 0x1C5, 0));
            trkCount = At(bytes, 0x1E6, 18);
            if (trkCount == 0) trkCount = 18;
            usrBase = 0x406;
            dataStart = 0x586;
        }
        else
        {
            tpqn = Le16(bytes, 0x20A);
            if (tpqn == 0) tpqn = 48;
            bpm = Le16(bytes, 0x20C);
            if (bpm == 0) bpm = 120;
            gblTransp = Sign8(At(bytes, 0x211, 0));
            trkCount = Le16(bytes, 0x208);
            if (trkCount == 0) trkCount = 18;
            usrBase = 0xB18;
            dataStart = 0xC98;
        }

        // ユーザー SysEx（8 個。各 0x30: 名前 0x18 + データ 0x18）
        var usr = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            int o = usrBase + i * 0x30 + 0x18;
            usr.Add(o + 0x18 <= bytes.Length ? bytes.AsSpan(o, 0x18).ToArray() : Array.Empty<byte>());
        }

        // トラックをたどってイベントを集める
        var raw = new List<Raw>();
        int seq = 0;
        int pos = dataStart;
        int parsed = 0;
        var trackNames = new List<string>();
        int hdrFixed = ver == 2 ? 0x2C : 0x2E;
        while (parsed < trkCount && pos + hdrFixed <= bytes.Length)
        {
            long trkLen;
            if (ver == 2)
            {
                int l = Le16(bytes, pos);
                trkLen = (l & ~0x03) | ((l & 0x03) << 16);
            }
            else trkLen = Le32(bytes, pos);
            if (trkLen < hdrFixed) break;
            int baseAt = pos;
            int end = (int)Math.Min((long)baseAt + trkLen, bytes.Length);
            int hdr = baseAt + (ver == 2 ? 2 : 4);
            DecodeTrack(bytes, ver, hdr, baseAt, end, gblTransp, usr, bpm, raw, ref seq, parsed);
            // トラックのコメント（36 バイト）を名前にする
            trackNames.Add(hdr + 0x2A <= bytes.Length ? Text.Decode(bytes.AsSpan(hdr + 0x06, 0x24)) : "");
            pos = (int)Math.Min((long)baseAt + trkLen, int.MaxValue - 0x100);
            parsed++;
        }
        if (parsed == 0) throw new MidiFormatException("トラックが読み取れませんでした");

        // ティック → マイクロ秒（テンポマップを適用）
        raw = raw.OrderBy(e => e.Tick).ThenBy(e => e.Seq).ToList();
        // ポートを 2 つ以上使っている曲は、A → GS、それ以外 → MT-32 に振り分ける
        var devs = raw.Where(e => !e.IsTempo).Select(e => e.Dev).Distinct().OrderBy(x => x).ToList();
        bool multi = devs.Count >= 2;
        double tpq = tpqn;
        uint tempo = (uint)(60_000_000.0 / bpm);
        long lastTick = 0;
        double lastUs = 0;
        var song = new Song { FirstBpm = bpm, TrackNames = trackNames };
        foreach (var e in raw)
        {
            double dt = e.Tick - lastTick;
            lastUs += dt * tempo / tpq;
            lastTick = e.Tick;
            long t = (long)Math.Round(lastUs, MidpointRounding.AwayFromZero);
            song.LengthUs = Math.Max(song.LengthUs, t);
            if (e.IsTempo) tempo = e.Tempo;
            else
            {
                Dest? byPort = multi ? (e.Dev == devs[0] ? Dest.Main : Dest.Mt32) : null;
                song.Events.Add(new TimedEvent(t, e.Data, e.Named ?? byPort ?? Dest.Song, e.Track));
            }
        }
        return song;
    }

    static void DecodeTrack(byte[] d, int ver, int hdr, int baseAt, int end, int gblTransp, List<byte[]> usr, int bpm, List<Raw> raw, ref int seq, int track)
    {
        // トラックヘッダ（固定 0x2A バイト）
        byte rawChn = At(d, hdr + 0x02, 0);
        byte transpRaw = At(d, hdr + 0x03, 0);
        int startTick0 = Sign8(At(d, hdr + 0x04, 0));
        byte mute = At(d, hdr + 0x05, 0);
        // トラックのコメント（36 バイト）に音源名が書いてあれば、そちらへ送る
        Dest? named = hdr + 0x2A <= d.Length ? Text.DestFromName(d.AsSpan(hdr + 0x06, 0x24)) : null;
        if (mute != 0) return; // ミュートトラックは出力しない

        byte midChn, midDev;
        if ((rawChn & 0x80) != 0) { midChn = 0; midDev = 0xFF; } // ヌルデバイス
        else { midChn = (byte)(rawChn & 0x0F); midDev = (byte)(rawChn >> 4); }

        int transp;
        if ((transpRaw & 0x80) != 0) transp = 0; // bit7 がリズムチャンネル → 移調しない
        else
        {
            int t = transpRaw;
            if ((transpRaw & 0x40) != 0) t -= 0x80;
            transp = t + gblTransp;
        }

        int step = ver == 2 ? 4 : 6;
        int ev = hdr + 0x2A;

        long cur = 0;
        long startTick = startTick0;
        if (startTick >= 0)
        {
            cur += startTick;
            startTick = 0;
        }

        int ip = ev;
        var measurePos = new List<int> { ev };
        int parentPos = 0;
        var loopPPos = new int[8];
        var loopPos = new int[8];
        var loopCnt = new uint[8];
        int loopIdx = 0;
        bool trkEnd = false;
        long guard = 0;

        // そのトラックで鳴っているノート: 音高 → (note-off の raw index, off tick)
        var active = new Dictionary<byte, (int idx, long off)>();

        // Roland の機種（DF で変える）。レコンポーザの既定は MT-32（デバイス 10h・モデル 16h）
        var gs = new byte[] { 0x10, 0x16, 0, 0, 0, 0 };
        var xg = new byte[6];

        int localSeq = seq;
        void Emit(byte[] bytes)
        {
            if (midDev != 0xFF)
            {
                raw.Add(new Raw { Tick = Math.Max(cur, 0), Seq = localSeq++, Data = bytes, Dev = midDev, Named = named, Track = track });
            }
        }
        void EmitSyx(List<byte> syx, byte p1, byte p2)
        {
            if (syx.Count == 0 || syx[^1] != 0xF7) syx.Add(0xF7);
            if (syx.Count > 1)
            {
                var full = new byte[syx.Count + 1];
                full[0] = 0xF0;
                syx.CopyTo(full, 1);
                Emit(full);
            }
        }

        while (ip + step <= end && !trkEnd)
        {
            if (++guard > 4_000_000) break;
            int prevPos = ip;
            byte cmd, p1, p2;
            long delay;
            long durat;
            if (ver == 2)
            {
                cmd = d[ip];
                delay = d[ip + 1];
                p1 = d[ip + 2];
                durat = d[ip + 2];
                p2 = d[ip + 3];
                ip += 4;
            }
            else
            {
                cmd = d[ip];
                p2 = d[ip + 1];
                delay = Le16(d, ip + 2);
                p1 = d[ip + 4];
                durat = Le16(d, ip + 4);
                ip += 6;
            }

            if (cmd < 0x80)
            {
                // ノート
                if (durat > 0 && midDev != 0xFF)
                {
                    byte note = (byte)((cmd + transp) & 0x7F);
                    long now = Math.Max(cur, 0);
                    foreach (var k in active.Where(kv => kv.Value.off <= now).Select(kv => kv.Key).ToList()) active.Remove(k);
                    if (active.TryGetValue(note, out var a))
                    {
                        // 鳴っている同じ音 → 消音位置を延ばす
                        long newOff = now + durat;
                        raw[a.idx].Tick = newOff;
                        active[note] = (a.idx, newOff);
                    }
                    else
                    {
                        raw.Add(new Raw { Tick = now, Seq = localSeq++, Data = new byte[] { (byte)(0x90 | midChn), note, p2 }, Dev = midDev, Named = named, Track = track });
                        int offIdx = raw.Count;
                        raw.Add(new Raw { Tick = now + durat, Seq = localSeq++, Data = new byte[] { (byte)(0x80 | midChn), note, 0 }, Dev = midDev, Named = named, Track = track });
                        active[note] = (offIdx, now + durat);
                    }
                }
            }
            else
            {
                switch (cmd)
                {
                    case >= 0x90 and <= 0x97:
                        // ユーザー SysEx
                        EmitSyx(ProcessRcpSysex(usr[cmd & 0x07], p1, p2, midChn), p1, p2);
                        break;
                    case 0x98:
                        // チャンネル Exclusive（継続行 F7 がデータ本体）
                        EmitSyx(ProcessRcpSysex(ReadMulti(d, ver, ref ip, end), p1, p2, midChn), p1, p2);
                        break;
                    case >= 0xC0 and <= 0xCF:
                        {
                            int idx = cmd & 0x0F;
                            if (cmd == 0xC6) Emit(new byte[] { 0xF0, 0x43, 0x75, midChn, 0x10, p1, p2, 0xF7 });
                            else if (cmd is 0xCA or 0xCB) Emit(new byte[] { 0xF0, 0x43, (byte)(0x10 | midChn), 0x10, (byte)(0x7B + (cmd - 0xCA)), p1, p2, 0xF7 });
                            else if (DxParam[idx] != 0xFF) Emit(new byte[] { 0xF0, 0x43, (byte)(0x10 | midChn), DxParam[idx], p1, p2, 0xF7 });
                            break;
                        }
                    case 0xD0: xg[2] = p1; xg[3] = p2; break;
                    case 0xD1: xg[0] = p1; xg[1] = p2; break;
                    case 0xD2:
                        xg[4] = p1; xg[5] = p2;
                        Emit(new byte[] { 0xF0, 0x43, xg[0], xg[1], xg[2], xg[3], xg[4], xg[5], 0xF7 });
                        break;
                    case 0xD3:
                        xg[4] = p1; xg[5] = p2;
                        Emit(new byte[] { 0xF0, 0x43, 0x10, 0x4C, xg[2], xg[3], xg[4], xg[5], 0xF7 });
                        break;
                    case 0xDC: Emit(new byte[] { 0xF0, 0x41, 0x32, midChn, p1, p2, 0xF7 }); break;
                    case 0xDD: gs[2] = p1; gs[3] = p2; break;
                    case 0xDE:
                        {
                            gs[4] = p1; gs[5] = p2;
                            uint chk = 0;
                            for (int i = 2; i < 6; i++) chk = (chk + gs[i]) & 0xFF;
                            Emit(new byte[] { 0xF0, 0x41, gs[0], gs[1], 0x12, gs[2], gs[3], gs[4], gs[5], (byte)((0x100 - chk) & 0x7F), 0xF7 });
                            break;
                        }
                    case 0xDF: gs[0] = p1; gs[1] = p2; break;
                    case 0xE1:
                        Emit(new byte[] { (byte)(0xB0 | midChn), 0x20, p2 });
                        Emit(new byte[] { (byte)(0xC0 | midChn), p1 });
                        break;
                    case 0xE2:
                        Emit(new byte[] { (byte)(0xB0 | midChn), 0x00, p2 });
                        Emit(new byte[] { (byte)(0xC0 | midChn), p1 });
                        break;
                    case 0xE6:
                        // MIDI チャンネル変更（1 始まり。0 はミュート）
                        if (p1 == 0) midDev = 0xFF;
                        else
                        {
                            midDev = (byte)((p1 - 1) >> 4);
                            midChn = (byte)((p1 - 1) & 0x0F);
                        }
                        break;
                    case 0xE7:
                        {
                            // テンポ（p1 = 倍率。40h=100%）。段階変化(p2)は非対応
                            ulong scale = Math.Max((ulong)p1, 1);
                            uint us = (uint)(60_000_000UL * 64 / ((ulong)bpm * scale));
                            raw.Add(new Raw { Tick = Math.Max(cur, 0), Seq = localSeq++, IsTempo = true, Tempo = us, Dev = 0, Named = null, Track = track });
                            break;
                        }
                    case 0xEA: Emit(new byte[] { (byte)(0xD0 | midChn), p1 }); break; // チャンネルアフタータッチ
                    case 0xEB: Emit(new byte[] { (byte)(0xB0 | midChn), p1, p2 }); break; // コントロールチェンジ
                    case 0xEC: if (p1 < 0x80) Emit(new byte[] { (byte)(0xC0 | midChn), p1 }); break; // 音色
                    case 0xED: Emit(new byte[] { (byte)(0xA0 | midChn), p1, p2 }); break; // ノートアフタータッチ
                    case 0xEE: Emit(new byte[] { (byte)(0xE0 | midChn), p1, p2 }); break; // ピッチベンド
                    case 0xF6:
                        // コメント：継続行を読み飛ばすだけ
                        ReadMulti(d, ver, ref ip, end);
                        delay = 0;
                        break;
                    case 0xF8:
                        // ループ終わり
                        if (loopIdx > 0)
                        {
                            loopIdx--;
                            loopCnt[loopIdx]++;
                            uint count = (uint)delay; // p0 = 繰り返し回数
                            bool take = count == 0 || count >= 0x7F ? loopCnt[loopIdx] < LoopInfiniteTimes : loopCnt[loopIdx] < count;
                            if (take)
                            {
                                parentPos = loopPPos[loopIdx];
                                ip = loopPos[loopIdx];
                                loopIdx++;
                            }
                        }
                        delay = 0;
                        break;
                    case 0xF9:
                        // ループ始まり
                        if (loopIdx < 8)
                        {
                            loopPPos[loopIdx] = parentPos;
                            loopPos[loopIdx] = ip;
                            loopCnt[loopIdx] = 0;
                            loopIdx++;
                        }
                        delay = 0;
                        break;
                    case 0xFC:
                        // 小節リピート
                        if (parentPos != 0)
                        {
                            ip = parentPos;
                            parentPos = 0;
                        }
                        else
                        {
                            ip -= step; // コマンド先頭に戻す
                            int iters = 0;
                            while (true)
                            {
                                if (++iters > 0x8000) break;
                                int measureId, repeatPos, next;
                                if (ver == 2)
                                {
                                    int d0 = d[ip + 1], d1 = d[ip + 2], d2 = d[ip + 3];
                                    measureId = d0 | ((d1 & 0x03) << 8);
                                    repeatPos = (d1 & ~0x03) | (d2 << 8);
                                    next = ip + 4;
                                }
                                else
                                {
                                    int d0 = Le16(d, ip + 2);
                                    int du = Le16(d, ip + 4);
                                    repeatPos = 0x2E + Math.Max(du - 0x30, 0) * 6;
                                    measureId = d0;
                                    next = ip + 6;
                                }
                                ip = next;
                                if (measureId >= measurePos.Count) break;
                                if (baseAt + repeatPos == prevPos) break; // 自分自身への再帰を防ぐ
                                if (parentPos == 0) parentPos = ip;
                                ip = baseAt + repeatPos;
                                if (ip + step > end || d[ip] != 0xFC) break;
                            }
                        }
                        delay = 0;
                        break;
                    case 0xFD:
                        // 小節の終わり
                        if (parentPos != 0)
                        {
                            ip = parentPos;
                            parentPos = 0;
                        }
                        if (measurePos.Count < 0x8000) measurePos.Add(ip);
                        else trkEnd = true;
                        delay = 0;
                        break;
                    case 0xFE:
                        trkEnd = true;
                        delay = 0;
                        break;
                    // 未対応コマンド（E5 キースキャン, F5 キーシグネチャ等）は読み飛ばす
                }
            }

            cur += delay;
            // 負の開始ティックを消化する
            if (startTick < 0 && cur > 0)
            {
                startTick += cur;
                if (startTick >= 0)
                {
                    cur = startTick;
                    startTick = 0;
                }
                else cur = 0;
            }
        }
        seq = localSeq;
    }

    /// <summary>継続コマンド（F7）を読み、データバイトを集める（初回行は含めない）。ip は継続行の分だけ進める</summary>
    static List<byte> ReadMulti(byte[] d, int ver, ref int ip, int end)
    {
        var o = new List<byte>();
        if (ver == 2)
        {
            while (ip + 4 <= end && d[ip] == 0xF7)
            {
                o.Add(d[ip + 2]);
                o.Add(d[ip + 3]);
                ip += 4;
            }
        }
        else
        {
            while (ip + 6 <= end && d[ip] == 0xF7)
            {
                for (int j = 0; j < 5; j++) o.Add(d[ip + 1 + j]);
                ip += 6;
            }
        }
        return o;
    }
}
