namespace MidiCore;

/// <summary>
/// LZH（LHA）書庫を読む。ヘッダーはレベル 0 / 1 / 2、圧縮方式は -lh0- -lh1- -lh4- -lh5- -lh6- -lh7- に対応。
/// Mac から来たファイルは前に MacBinary の 128 バイトが付いていたり、自己解凍形式は前に実行部分が付いていたりするので、
/// 先頭から順に「-lh5- のような圧縮方式の名前があって、ヘッダーのチェックサムも合う」位置を探して読み始める。
/// </summary>
public static class Lzh
{
    public sealed record Item(string Name, string Method, int DataOffset, int CompressedSize, int OriginalSize, ushort Crc, bool IsDirectory);

    /// <summary>書庫の中のファイルの一覧（壊れていたら、そこまで）</summary>
    public static List<Item> List(byte[] d)
    {
        var list = new List<Item>();
        int i = FindStart(d);
        if (i < 0) throw new MidiFormatException("LZH として読めません（ヘッダーが見つかりません）");
        // 終わりの印は 0 の 1 バイト（圧縮方式の名前が続かなければ終わり）
        while (i + 22 <= d.Length && IsMethod(d, i + 2))
        {
            var item = ReadHeader(d, i, out int next);
            if (item == null) break;
            list.Add(item);
            i = next;
        }
        if (list.Count == 0) throw new MidiFormatException("LZH として読めません");
        return list;
    }

    /// <summary>1 ファイルを展開する（CRC も確かめる）</summary>
    public static byte[] Extract(byte[] d, Item it)
    {
        int dicbit;
        switch (it.Method)
        {
            case "-lh0-":
                var raw = d.AsSpan(it.DataOffset, Math.Min(it.CompressedSize, d.Length - it.DataOffset)).ToArray();
                CheckCrc(raw, it);
                return raw;
            case "-lh1-": dicbit = 12; break;
            case "-lh4-": dicbit = 12; break;
            case "-lh5-": dicbit = 13; break;
            case "-lh6-": dicbit = 15; break;
            case "-lh7-": dicbit = 16; break;
            default: throw new MidiFormatException($"この圧縮方式（{it.Method}）には対応していません");
        }
        var src = new BitReader(d, it.DataOffset, Math.Min(it.CompressedSize, d.Length - it.DataOffset));
        var outp = it.Method == "-lh1-" ? DecodeLh1(src, it.OriginalSize) : DecodeStatic(src, it.OriginalSize, dicbit);
        CheckCrc(outp, it);
        return outp;
    }

    static void CheckCrc(byte[] b, Item it)
    {
        if (b.Length != it.OriginalSize || Crc16(b) != it.Crc)
            throw new MidiFormatException($"{it.Name} が壊れています（CRC 不一致）");
    }

    // ---------------------------------------------------------------- ヘッダー

    static ushort U16(byte[] d, int i) => (ushort)(d[i] | d[i + 1] << 8);
    static int U32(byte[] d, int i) => d[i] | d[i + 1] << 8 | d[i + 2] << 16 | d[i + 3] << 24;

    static bool IsMethod(byte[] d, int i) =>
        i + 7 <= d.Length && d[i] == '-' && d[i + 1] == 'l' && (d[i + 2] == 'h' || d[i + 2] == 'z') && d[i + 4] == '-';

    /// <summary>最初のヘッダーの位置を探す</summary>
    static int FindStart(byte[] d)
    {
        int end = Math.Min(d.Length - 24, 256 * 1024);
        for (int i = 0; i <= end; i++)
        {
            if (!IsMethod(d, i + 2)) continue;
            switch (d[i + 20])
            {
                case 0 or 1:
                    int n = d[i];
                    if (n < 20 || i + 2 + n > d.Length) continue;
                    byte sum = 0;
                    for (int k = 0; k < n; k++) sum += d[i + 2 + k];
                    if (sum == d[i + 1]) return i;
                    break;
                case 2:
                    if (U16(d, i) >= 26) return i;
                    break;
            }
        }
        return -1;
    }

    static Item? ReadHeader(byte[] d, int i, out int next)
    {
        next = d.Length;
        if (!IsMethod(d, i + 2)) return null;
        string method = System.Text.Encoding.ASCII.GetString(d, i + 2, 5);
        int csize = U32(d, i + 7), osize = U32(d, i + 11);
        int level = d[i + 20];
        byte[] file = Array.Empty<byte>(), dir = Array.Empty<byte>();
        ushort crc;
        int dataOffset;

        // 拡張ヘッダー：[大きさ 2][種類 1][中身]…。次の大きさは前の拡張ヘッダーの最後の 2 バイト
        int ReadExt(int j, int size, out int total)
        {
            total = 0;
            while (size != 0 && j + size <= d.Length)
            {
                byte type = d[j];
                var data = d.AsSpan(j + 1, size - 3).ToArray();
                if (type == 0x01) file = data;
                else if (type == 0x02) dir = data;
                total += size;
                int nextSize = U16(d, j + size - 2);
                j += size;
                size = nextSize;
            }
            return j;
        }

        switch (level)
        {
            case 0:
            {
                int hs = d[i], nl = d[i + 21];
                file = d.AsSpan(i + 22, nl).ToArray();
                crc = U16(d, i + 22 + nl);
                dataOffset = i + 2 + hs;
                next = dataOffset + csize;
                break;
            }
            case 1:
            {
                int hs = d[i], nl = d[i + 21];
                file = d.AsSpan(i + 22, nl).ToArray();
                crc = U16(d, i + 22 + nl);
                int j = i + 2 + hs;
                dataOffset = ReadExt(j, U16(d, j - 2), out int extTotal);
                // レベル 1 では、圧縮後の大きさに拡張ヘッダーも含まれる
                csize -= extTotal;
                next = dataOffset + csize;
                break;
            }
            case 2:
            {
                int total = U16(d, i);
                crc = U16(d, i + 21);
                ReadExt(i + 26, U16(d, i + 24), out _);
                dataOffset = i + total;
                next = dataOffset + csize;
                break;
            }
            default:
                return null;
        }
        if (csize < 0 || dataOffset > d.Length) return null;
        bool isDir = method == "-lhd-";
        return new Item(PathName(dir, file), method, dataOffset, csize, osize, crc, isDir);
    }

    /// <summary>
    /// フォルダ部分とファイル名部分をつないで、区切り '/' の名前にする。区切りは 0xFF（拡張ヘッダー）か '\'。
    /// '\' は Shift_JIS の 2 バイト目にも現れる（「ソ」「表」など）ので、2 バイト文字の後半は区切りにしない。
    /// </summary>
    public static string PathName(byte[] dir, byte[] file)
    {
        var raw = new List<byte>();
        foreach (var part in new[] { dir, file })
        {
            if (part.Length == 0) continue;
            if (raw.Count > 0 && raw[^1] != '/') raw.Add((byte)'/');
            bool second = false;
            foreach (byte c in part)
            {
                if (second) { raw.Add(c); second = false; }
                else if (c == 0xFF || c == '\\') raw.Add((byte)'/');
                else
                {
                    raw.Add(c);
                    second = c is >= 0x81 and <= 0x9F or >= 0xE0 and <= 0xFC;
                }
            }
        }
        var b = raw.ToArray();
        try { return new System.Text.UTF8Encoding(false, true).GetString(b); }
        catch (System.Text.DecoderFallbackException) { return Text.ShiftJis.GetString(b); }
    }

    // ---------------------------------------------------------------- CRC

    static readonly ushort[] CrcTable = MakeCrcTable();

    static ushort[] MakeCrcTable()
    {
        var t = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            int r = i;
            for (int k = 0; k < 8; k++) r = (r & 1) != 0 ? (r >> 1) ^ 0xA001 : r >> 1;
            t[i] = (ushort)r;
        }
        return t;
    }

    public static ushort Crc16(byte[] b)
    {
        int crc = 0;
        foreach (byte x in b) crc = CrcTable[(crc ^ x) & 0xFF] ^ (crc >> 8);
        return (ushort)crc;
    }

    // ---------------------------------------------------------------- ビット読み

    sealed class BitReader
    {
        readonly byte[] _d;
        int _pos;
        readonly int _end;
        int _bit; // 今のバイトの中の位置（0 = 最上位）

        public BitReader(byte[] d, int start, int len) { _d = d; _pos = start; _end = start + len; }

        public int Bit()
        {
            int v = _pos < _end ? (_d[_pos] >> (7 - _bit)) & 1 : 0;
            if (++_bit == 8) { _bit = 0; _pos++; }
            return v;
        }

        public int Bits(int n)
        {
            int v = 0;
            for (int k = 0; k < n; k++) v = v << 1 | Bit();
            return v;
        }
    }

    // ---------------------------------------------------------------- 静的ハフマン（-lh4- ～ -lh7-）

    const int NC = 255 + 256 + 2 - 3; // 文字 256 + 長さ 3～256
    const int NT = 19;
    const int TBIT = 5;
    const int CBIT = 9;

    /// <summary>符号の長さの表から作る、正準ハフマンの復号器</summary>
    sealed class Huffman
    {
        readonly int[] _count = new int[17];
        readonly int[] _symbols;
        readonly int _fixed = -1;

        public Huffman(int[] len)
        {
            var syms = new List<int>();
            for (int l = 1; l <= 16; l++)
                for (int s = 0; s < len.Length; s++)
                    if (len[s] == l) { syms.Add(s); _count[l]++; }
            _symbols = syms.ToArray();
            if (_symbols.Length == 1) _fixed = _symbols[0];
        }

        public Huffman(int only) { _symbols = new[] { only }; _fixed = only; }

        public int Decode(BitReader r)
        {
            if (_fixed >= 0 && _symbols.Length == 1 && _count.Sum() <= 1)
            {
                // 記号が 1 つだけ：長さ 0 なら何も読まない、長さがあればその分読み飛ばす
                for (int l = 1; l <= 16; l++) if (_count[l] == 1) { r.Bits(l); break; }
                return _fixed;
            }
            int code = 0, first = 0, index = 0;
            for (int l = 1; l <= 16; l++)
            {
                code |= r.Bit();
                int n = _count[l];
                if (code - first < n) return _symbols[index + code - first];
                index += n;
                first = (first + n) << 1;
                code <<= 1;
            }
            throw new MidiFormatException("LZH の圧縮データが壊れています");
        }
    }

    static Huffman ReadPtLen(BitReader r, int nn, int nbit, int special)
    {
        int n = r.Bits(nbit);
        if (n == 0) return new Huffman(r.Bits(nbit));
        var len = new int[nn];
        int i = 0;
        while (i < n && i < nn)
        {
            int c = r.Bits(3);
            if (c == 7) while (r.Bit() == 1) c++;
            len[i++] = c;
            if (i == special)
            {
                int z = r.Bits(2);
                while (z-- > 0 && i < nn) len[i++] = 0;
            }
        }
        return new Huffman(len);
    }

    static Huffman ReadCLen(BitReader r, Huffman pt)
    {
        int n = r.Bits(CBIT);
        if (n == 0) return new Huffman(r.Bits(CBIT));
        var len = new int[NC];
        int i = 0;
        while (i < n && i < NC)
        {
            int c = pt.Decode(r);
            if (c <= 2)
            {
                int z = c == 0 ? 1 : c == 1 ? r.Bits(4) + 3 : r.Bits(CBIT) + 20;
                while (z-- > 0 && i < NC) len[i++] = 0;
            }
            else len[i++] = c - 2;
        }
        return new Huffman(len);
    }

    static byte[] DecodeStatic(BitReader r, int size, int dicbit)
    {
        int np = dicbit + 1, pbit = dicbit <= 13 ? 4 : 5;
        var o = new byte[size];
        int pos = 0, block = 0;
        Huffman? c = null, p = null;
        while (pos < size)
        {
            if (block == 0)
            {
                block = r.Bits(16);
                var pt = ReadPtLen(r, NT, TBIT, 3);
                c = ReadCLen(r, pt);
                p = ReadPtLen(r, np, pbit, -1);
            }
            block--;
            int ch = c!.Decode(r);
            if (ch < 256) { o[pos++] = (byte)ch; continue; }
            int len = ch - 253;
            int j = p!.Decode(r);
            int dist = j == 0 ? 0 : (1 << (j - 1)) + r.Bits(j - 1);
            Copy(o, ref pos, dist + 1, len);
        }
        return o;
    }

    /// <summary>dist バイト前から len バイト写す。書庫の作り方で、最初の窓は空白（0x20）で埋まっている</summary>
    static void Copy(byte[] o, ref int pos, int dist, int len)
    {
        for (int k = 0; k < len && pos < o.Length; k++, pos++)
        {
            int from = pos - dist;
            o[pos] = from >= 0 ? o[from] : (byte)0x20;
        }
    }

    // ---------------------------------------------------------------- 適応ハフマン（-lh1-、LHarc 1.x）

    const int N_CHAR = 256 + 60 - 3 + 1; // 314
    const int T = N_CHAR * 2 - 1;
    const int R = T - 1;
    const int MAX_FREQ = 0x8000;

    static byte[] DecodeLh1(BitReader r, int size)
    {
        var freq = new int[T + 1];
        var son = new int[T];
        var prnt = new int[T + N_CHAR];
        for (int k = 0; k < N_CHAR; k++) { freq[k] = 1; son[k] = k + T; prnt[k + T] = k; }
        for (int k = 0, j = N_CHAR; j <= R; k += 2, j++)
        {
            freq[j] = freq[k] + freq[k + 1];
            son[j] = k;
            prnt[k] = prnt[k + 1] = j;
        }
        freq[T] = 0xFFFF;
        prnt[R] = 0;

        void Reconst()
        {
            int j = 0;
            for (int k = 0; k < T; k++)
                if (son[k] >= T) { freq[j] = (freq[k] + 1) / 2; son[j] = son[k]; j++; }
            for (int k = 0, jj = N_CHAR; jj < T; k += 2, jj++)
            {
                int f = freq[jj] = freq[k] + freq[k + 1];
                int l = jj - 1;
                while (f < freq[l]) l--;
                l++;
                Array.Copy(freq, l, freq, l + 1, jj - l);
                freq[l] = f;
                Array.Copy(son, l, son, l + 1, jj - l);
                son[l] = k;
            }
            for (int k = 0; k < T; k++)
            {
                int s = son[k];
                if (s >= T) prnt[s] = k;
                else prnt[s] = prnt[s + 1] = k;
            }
        }

        void Update(int c)
        {
            if (freq[R] == MAX_FREQ) Reconst();
            c = prnt[c + T];
            do
            {
                int k = ++freq[c];
                int l = c + 1;
                if (k > freq[l])
                {
                    while (k > freq[++l]) { }
                    l--;
                    freq[c] = freq[l];
                    freq[l] = k;
                    int i = son[c];
                    prnt[i] = l;
                    if (i < T) prnt[i + 1] = l;
                    int j = son[l];
                    son[l] = i;
                    prnt[j] = c;
                    if (j < T) prnt[j + 1] = c;
                    son[c] = j;
                    c = l;
                }
            } while ((c = prnt[c]) != 0);
        }

        // 位置の上位 6 ビットの符号（長さ 3 が 1 個、4 が 3 個、5 が 8 個、6 が 12 個、7 が 24 個、8 が 16 個）
        var plen = new int[64];
        {
            int[] bounds = { 1, 4, 12, 24, 48, 64 };
            int l = 3, k = 0;
            foreach (int b in bounds) { for (; k < b; k++) plen[k] = l; l++; }
        }
        var ph = new Huffman(plen);

        var o = new byte[size];
        int pos = 0;
        while (pos < size)
        {
            int c = son[R];
            while (c < T) c = son[c + r.Bit()];
            c -= T;
            Update(c);
            if (c < 256) { o[pos++] = (byte)c; continue; }
            int len = c - 253;
            int up = ph.Decode(r);
            int dist = (up << 6) | r.Bits(6);
            Copy(o, ref pos, dist + 1, len);
        }
        return o;
    }
}
