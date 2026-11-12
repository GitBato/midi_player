namespace MidiCore;

/// <summary>
/// Standard MIDI File (SMF) の読み込み。フォーマット 0 / 1（2 も全トラックを重ねて扱う）に対応し、
/// テンポマップを適用して「再生時刻（マイクロ秒）付きのイベント列」に変換する。
/// </summary>
public static class Smf
{
    enum Kind { Send, Tempo, End }

    sealed class RawEvent
    {
        public long Tick;
        public int Track;
        public int Seq;
        public Kind Kind;
        public byte[] Data = Array.Empty<byte>();
        public uint Tempo;
    }

    /// <summary>トラックから読み取った、送り先の手がかり</summary>
    sealed class TrackInfo
    {
        public byte? Port;      // FF 21
        public Dest? Device;    // FF 09
        public Dest? Name;      // FF 03
        public string? Title;   // 最初の FF 03
    }

    sealed class Reader
    {
        readonly byte[] _buf;
        public int Pos;
        public int End;
        public Reader(byte[] buf, int pos, int end) { _buf = buf; Pos = pos; End = end; }
        public byte U8()
        {
            if (Pos >= End) throw new MidiFormatException("データが途中で終わっています");
            return _buf[Pos++];
        }
        public ReadOnlySpan<byte> Bytes(int n)
        {
            if (n < 0 || Pos + n > End) throw new MidiFormatException("データが途中で終わっています");
            var s = _buf.AsSpan(Pos, n);
            Pos += n;
            return s;
        }
        public ushort U16() { var b = Bytes(2); return (ushort)(b[0] << 8 | b[1]); }
        public uint U32() { var b = Bytes(4); return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]); }
        /// <summary>可変長数値（最大 4 バイト）</summary>
        public uint Vlq()
        {
            uint v = 0;
            for (int i = 0; i < 4; i++)
            {
                byte b = U8();
                v = (v << 7) | (uint)(b & 0x7F);
                if ((b & 0x80) == 0) return v;
            }
            throw new MidiFormatException("可変長数値が不正です");
        }
        public bool Done => Pos >= End;
    }

    static int DataLen(byte status) => (status & 0xF0) is 0xC0 or 0xD0 ? 1 : 2;

    /// <summary>トラックごとのポート番号（2 つ以上使われている曲だけ）を送り先にする。一番小さいポート → GS、それ以外 → MT-32</summary>
    public static Dest?[] DestFromPorts(IReadOnlyList<byte?> ports)
    {
        var used = ports.Where(p => p.HasValue).Select(p => p!.Value).Distinct().OrderBy(p => p).ToList();
        var r = new Dest?[ports.Count];
        if (used.Count < 2) return r;
        for (int i = 0; i < ports.Count; i++)
            if (ports[i] is byte p) r[i] = p == used[0] ? Dest.Main : Dest.Mt32;
        return r;
    }

    static void ParseTrack(byte[] data, int start, int end, int track, List<RawEvent> outList, TrackInfo info)
    {
        var r = new Reader(data, start, end);
        long tick = 0;
        byte? running = null;
        int seq = 0;
        void Push(long t, Kind k, byte[]? d = null, uint tempo = 0) =>
            outList.Add(new RawEvent { Tick = t, Track = track, Seq = seq++, Kind = k, Data = d ?? Array.Empty<byte>(), Tempo = tempo });
        while (!r.Done)
        {
            tick += r.Vlq();
            byte first = r.U8();
            if (first == 0xFF)
            {
                // メタイベント
                byte ty = r.U8();
                int len = (int)r.Vlq();
                var body = r.Bytes(len);
                switch (ty)
                {
                    case 0x21 when len >= 1 && info.Port == null: info.Port = body[0]; break;
                    case 0x09 when info.Device == null: info.Device = Text.DestFromName(body); break;
                    case 0x03:
                        info.Name ??= Text.DestFromName(body);
                        info.Title ??= Text.Decode(body);
                        break;
                    case 0x51 when len == 3:
                        uint t = (uint)(body[0] << 16 | body[1] << 8 | body[2]);
                        if (t > 0) Push(tick, Kind.Tempo, tempo: t);
                        break;
                    case 0x2F:
                        Push(tick, Kind.End);
                        return;
                }
                // 規格ではメタイベントでランニングステータスは切れるが、切らずに続けて書いてある SMF が多いので残す
            }
            else if (first is 0xF0 or 0xF7)
            {
                // SysEx（F0 は先頭バイトを補う。F7 はそのまま送る）
                int len = (int)r.Vlq();
                var body = r.Bytes(len);
                var msg = new List<byte>(len + 1);
                if (first == 0xF0) msg.Add(0xF0);
                msg.AddRange(body.ToArray());
                if (msg.Count > 0) Push(tick, Kind.Send, msg.ToArray());
            }
            else if (first >= 0x80 && first <= 0xEF)
            {
                running = first;
                var msg = new byte[1 + DataLen(first)];
                msg[0] = first;
                for (int i = 1; i < msg.Length; i++) msg[i] = (byte)(r.U8() & 0x7F);
                Push(tick, Kind.Send, msg);
            }
            else if (first <= 0x7F)
            {
                // ランニングステータス
                byte status = running ?? throw new MidiFormatException("ランニングステータスの前にステータスバイトがありません");
                var msg = new byte[1 + DataLen(status)];
                msg[0] = status;
                msg[1] = first;
                for (int i = 2; i < msg.Length; i++) msg[i] = (byte)(r.U8() & 0x7F);
                Push(tick, Kind.Send, msg);
            }
            else
            {
                throw new MidiFormatException($"未対応のステータス 0x{first:X2}");
            }
        }
    }

    public static Song Parse(byte[] bytes)
    {
        var r = new Reader(bytes, 0, bytes.Length);
        // RIFF 形式（.rmi）や MacBinary の見出しは飛ばす（頭の 1 KB の中に MThd があれば、そこから読む）
        if (!StartsWith(bytes, 0, "MThd"))
        {
            int lim = Math.Min(bytes.Length, 1024);
            for (int i = 0; i + 4 <= lim; i++)
                if (StartsWith(bytes, i, "MThd")) { r.Pos = i; break; }
        }
        var sig = r.Bytes(4).ToArray();
        // 先頭の "MThd" だけが壊れて 0 になっているファイルも、続きが SMF の形なら読む
        bool brokenSig = sig.All(b => b == 0) && bytes.Length >= 18
            && bytes[4] == 0 && bytes[5] == 0 && bytes[6] == 0 && bytes[7] == 6 && StartsWith(bytes, 14, "MTrk");
        if (!StartsWith(sig, 0, "MThd") && !brokenSig)
        {
            if (StartsWith(bytes, 0, "MSCF"))
                throw new MidiFormatException("中身は CAB の圧縮ファイルで、MIDI ではありません（展開してから開いてください）");
            throw new MidiFormatException("SMF ではありません（MThd がありません）");
        }
        int hlen = (int)r.U32();
        if (hlen < 6) throw new MidiFormatException("ヘッダが短すぎます");
        int format = r.U16();
        int ntrks = r.U16();
        int division = r.U16();
        r.Bytes(hlen - 6);

        var raw = new List<RawEvent>();
        var infos = new List<TrackInfo>();
        int track = 0;
        while (track < ntrks && !r.Done)
        {
            var id = r.Bytes(4).ToArray();
            int len = (int)Math.Min(r.U32(), (uint)(r.End - r.Pos));
            int bodyStart = r.Pos;
            r.Bytes(len);
            if (StartsWith(id, 0, "MTrk"))
            {
                var info = new TrackInfo();
                ParseTrack(bytes, bodyStart, bodyStart + len, track, raw, info);
                infos.Add(info);
                track++;
            }
        }
        if (track == 0) throw new MidiFormatException("トラックがありません");

        // フォーマット 2 は各トラックを順につなげる
        if (format == 2)
        {
            long offset = 0;
            for (int t = 0; t < track; t++)
            {
                long end = raw.Where(e => e.Track == t).Select(e => e.Tick).DefaultIfEmpty(0).Max();
                foreach (var e in raw.Where(e => e.Track == t)) e.Tick += offset;
                offset += end;
            }
        }

        raw = raw.OrderBy(e => e.Tick).ThenBy(e => e.Track).ThenBy(e => e.Seq).ToList();

        // トラックごとの送り先：機器名 → トラック名 → ポート番号（2 つ以上使っているとき）→ 曲全体
        var byPort = DestFromPorts(infos.Select(i => i.Port).ToList());
        var dests = infos.Select((i, k) => i.Device ?? i.Name ?? byPort[k] ?? Dest.Song).ToList();

        // テンポマップを適用してマイクロ秒に変換
        bool smpte = (division & 0x8000) != 0;
        double tpq, usPerTickSmpte;
        if (smpte)
        {
            double fps = -(sbyte)(byte)(division >> 8);
            if (fps == 29.0) fps = 29.97;
            double tpf = division & 0xFF;
            tpq = 1.0;
            usPerTickSmpte = 1_000_000.0 / Math.Max(fps * tpf, 1.0);
        }
        else
        {
            tpq = Math.Max(division, 1);
            usPerTickSmpte = 0;
        }
        uint tempo = 500_000;
        uint? firstTempo = null;
        long lastTick = 0;
        double lastUs = 0;
        var song = new Song();
        foreach (var e in raw)
        {
            double dt = e.Tick - lastTick;
            lastUs += smpte ? dt * usPerTickSmpte : dt * tempo / tpq;
            lastTick = e.Tick;
            long t = (long)Math.Round(lastUs, MidpointRounding.AwayFromZero);
            song.LengthUs = Math.Max(song.LengthUs, t);
            switch (e.Kind)
            {
                case Kind.Tempo:
                    if (firstTempo == null && e.Tick == 0) firstTempo = e.Tempo;
                    tempo = e.Tempo;
                    break;
                case Kind.Send:
                    song.Events.Add(new TimedEvent(t, e.Data, e.Track < dests.Count ? dests[e.Track] : Dest.Song, e.Track));
                    break;
            }
        }
        song.FirstBpm = 60_000_000.0 / (firstTempo ?? 500_000);
        song.TrackNames = infos.Select(i => i.Title ?? "").ToList();
        return song;
    }

    internal static bool StartsWith(byte[] b, int at, string s)
    {
        if (at < 0 || at + s.Length > b.Length) return false;
        for (int i = 0; i < s.Length; i++) if (b[at + i] != (byte)s[i]) return false;
        return true;
    }
}
