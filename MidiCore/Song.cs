using System.Text;

namespace MidiCore;

/// <summary>イベントの送り先。トラックの持つ情報（ポート番号・機器名・トラック名）から決める</summary>
public enum Dest
{
    /// <summary>曲全体の送り先に従う</summary>
    Song,
    /// <summary>GS（メインの出口。SC-88 など）</summary>
    Main,
    /// <summary>MT-32 の出口</summary>
    Mt32,
    /// <summary>XG 音源の出口</summary>
    Xg,
    /// <summary>その他の音源（TX7 など）の出口</summary>
    Other,
}

/// <summary>再生時刻付きの 1 イベント（そのまま MIDI OUT に送れるバイト列）</summary>
public sealed class TimedEvent
{
    public long TimeUs;
    public byte[] Data;
    public Dest Dest;
    /// <summary>どのトラックのイベントか（Song.TrackNames の番号。トラックの無い形式は 0）</summary>
    public int Track;

    public TimedEvent(long timeUs, byte[] data, Dest dest = Dest.Song, int track = 0)
    {
        TimeUs = timeUs;
        Data = data;
        Dest = dest;
        Track = track;
    }
}

public sealed class Song
{
    public List<TimedEvent> Events = new();
    /// <summary>曲の長さ（最後のイベントまたは End of Track の時刻）</summary>
    public long LengthUs;
    /// <summary>最初のテンポ（BPM）</summary>
    public double FirstBpm = 120;
    /// <summary>読み込み時に音源へ送る音色データ（SNG のカスタム MT-32 音色・CM6 など）</summary>
    public List<byte[]> TimbreSysex = new();
    /// <summary>トラックの名前（TimedEvent.Track の番号ごと）</summary>
    public List<string> TrackNames = new();
}

public static class Text
{
    static Text() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static Encoding ShiftJis => Encoding.GetEncoding(932);

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>曲の中の文字（UTF-8 でなければ Shift_JIS として読む）。NUL から後ろと制御文字は落とす</summary>
    public static string Decode(ReadOnlySpan<byte> b)
    {
        int nul = b.IndexOf((byte)0);
        if (nul >= 0) b = b[..nul];
        string s;
        try { s = StrictUtf8.GetString(b); }
        catch (DecoderFallbackException) { s = ShiftJis.GetString(b); }
        var sb = new StringBuilder();
        foreach (var c in s) if (!char.IsControl(c)) sb.Append(c);
        return sb.ToString().Trim();
    }

    /// <summary>機器名・トラック名に書かれた音源の名前から送り先を決める（大文字小文字は区別しない）</summary>
    public static Dest? DestFromName(ReadOnlySpan<byte> name)
    {
        var up = new byte[name.Length];
        for (int i = 0; i < name.Length; i++) up[i] = name[i] is >= (byte)'a' and <= (byte)'z' ? (byte)(name[i] - 32) : name[i];
        static bool AlNum(byte c) => (c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
        // 単語の頭で始まるものだけ（"DISC-" の "SC-" などを拾わない）
        bool Has(string w)
        {
            var wb = Encoding.ASCII.GetBytes(w);
            for (int i = 0; i + wb.Length <= up.Length; i++)
                if (up.AsSpan(i, wb.Length).SequenceEqual(wb) && (i == 0 || !AlNum(up[i - 1]))) return true;
            return false;
        }
        bool hasMu = false, hasXg = false;
        for (int i = 0; i + 3 <= up.Length; i++)
            if (up[i] == 'M' && up[i + 1] == 'U' && ((up[i + 2] >= '0' && up[i + 2] <= '9') || up[i + 2] == '-') && (i == 0 || !AlNum(up[i - 1]))) hasMu = true;
        for (int i = 0; i + 2 <= up.Length; i++)
            if (up[i] == 'X' && up[i + 1] == 'G' && (i == 0 || !AlNum(up[i - 1])) && (i + 2 >= up.Length || !char.IsAsciiLetter((char)up[i + 2]))) hasXg = true;
        if (new[] { "TX7", "TX-7", "DX7", "DX-7", "TX816", "TX802" }.Any(Has)) return Dest.Other;
        if (new[] { "MT-32", "MT32", "CM-32", "CM32", "CM-64", "CM64", "LAPC" }.Any(Has)) return Dest.Mt32;
        if (hasMu || hasXg) return Dest.Xg;
        if (new[] { "SC-", "SC55", "SC88", "SOUND CANVAS", "SOUNDCANVAS" }.Any(Has)) return Dest.Main;
        return null;
    }
}

/// <summary>読み込みの失敗（理由を画面に出す）</summary>
public sealed class MidiFormatException : Exception
{
    public MidiFormatException(string message) : base(message) { }
}
