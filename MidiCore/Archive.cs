using System.IO.Compression;

namespace MidiCore;

/// <summary>
/// ZIP や LZH の中の曲を、展開せずにそのまま開く。古い日本の書庫はファイル名が Shift_JIS なので、Shift_JIS として読む
/// （UTF-8 の印が付いた名前は UTF-8 のまま）。
/// 書庫の中の書庫（「全曲集.lzh」の中に曲ごとの .lzh、など）は 1 段だけ中まで見る。
/// </summary>
public sealed class Archive
{
    /// <summary>曲として開ける拡張子</summary>
    static readonly string[] SongExt = { "mid", "midi", "smf", "kar", "rmi", "rcp", "r36", "g36", "sng", "mm2", "mmc", "cm6", "dxx" };

    /// <summary>Name は表示用の名前。Key は書庫の中の場所（ZIP は中の名前、LZH は番号。書庫の中の書庫は「外\n種類\n中」）</summary>
    public sealed record Entry(string Name, string Key);

    public string Path { get; }
    public List<Entry> Entries { get; }

    Archive(string path, List<Entry> entries) { Path = path; Entries = entries; }

    enum Kind { None, Zip, Lzh }

    static Kind KindOf(string name) => System.IO.Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".zip" => Kind.Zip,
        ".lzh" or ".lha" => Kind.Lzh,
        _ => Kind.None,
    };

    /// <summary>中の曲の一覧を出す書庫（ZIP・LZH）か</summary>
    public static bool IsArchive(string path) => KindOf(path) != Kind.None;

    static bool IsSong(string name) => SongExt.Contains(System.IO.Path.GetExtension(name).TrimStart('.').ToLowerInvariant());

    static byte[] ToBytes(Stream s)
    {
        if (s is MemoryStream m) return m.ToArray();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>書庫の中の全ファイル（フォルダ以外）の名前と場所。ZIP は大きいことがあるので、ファイルのまま読む</summary>
    static List<(string Name, string Key)> ListIn(Kind k, Stream s)
    {
        var list = new List<(string, string)>();
        if (k == Kind.Zip)
        {
            using var z = new ZipArchive(s, ZipArchiveMode.Read, false, Text.ShiftJis);
            foreach (var e in z.Entries)
                if (!e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\')) list.Add((e.FullName, e.FullName));
        }
        else
        {
            using (s)
            {
                var items = Lzh.List(ToBytes(s));
                for (int i = 0; i < items.Count; i++)
                    if (!items[i].IsDirectory) list.Add((items[i].Name, i.ToString()));
            }
        }
        return list;
    }

    static byte[] ReadIn(Kind k, Stream s, string key)
    {
        if (k == Kind.Zip)
        {
            using var z = new ZipArchive(s, ZipArchiveMode.Read, false, Text.ShiftJis);
            var entry = z.GetEntry(key) ?? throw new MidiFormatException($"{key} が見つかりません");
            using var es = entry.Open();
            return ToBytes(es);
        }
        using (s)
        {
            var data = ToBytes(s);
            var items = Lzh.List(data);
            return Lzh.Extract(data, items[int.Parse(key)]);
        }
    }

    /// <summary>書庫を開いて、曲として開けるファイルの一覧を作る（名前順）</summary>
    public static Archive Open(string path)
    {
        var k = KindOf(path);
        if (k == Kind.None) throw new MidiFormatException("ZIP・LZH ではありません");
        var all = ListIn(k, File.OpenRead(path));
        var list = new List<Entry>();
        foreach (var (name, key) in all)
        {
            if (IsSong(name)) { list.Add(new Entry(name, key)); continue; }
            var k2 = KindOf(name);
            if (k2 == Kind.None) continue;
            // 書庫の中の書庫。読めなければ飛ばす
            try
            {
                var inner = ReadIn(k, File.OpenRead(path), key);
                foreach (var (n2, key2) in ListIn(k2, new MemoryStream(inner)))
                    if (IsSong(n2)) list.Add(new Entry($"{name}/{n2}", $"{key}\n{k2}\n{key2}"));
            }
            catch (Exception) { }
        }
        if (list.Count == 0)
        {
            // 何が入っているかを添える（MDI や MML など、対応していない形式の曲のことがある）
            var exts = all.Select(a => System.IO.Path.GetExtension(a.Name).ToUpperInvariant())
                .Select(e => e == "" ? "（拡張子なし）" : e).Distinct().ToList();
            var more = exts.Count > 8 ? " ほか" : "";
            throw new MidiFormatException($"この書庫には再生できる曲のファイルが入っていません（中身: {string.Join(" ", exts.Take(8))}{more}）");
        }
        // 全部が同じフォルダに入っていたら、そのフォルダ名は表示しない
        int slash = list[0].Name.IndexOf('/');
        if (slash > 0)
        {
            var top = list[0].Name[..(slash + 1)];
            if (list.All(e => e.Name.StartsWith(top, StringComparison.Ordinal)))
                list = list.Select(e => e with { Name = e.Name[top.Length..] }).ToList();
        }
        list = list.OrderBy(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
        return new Archive(path, list);
    }

    /// <summary>書庫の中の 1 ファイルを読む</summary>
    public byte[] Read(Entry e)
    {
        var parts = e.Key.Split('\n');
        var b = ReadIn(KindOf(Path), File.OpenRead(Path), parts[0]);
        return parts.Length == 3 ? ReadIn(Enum.Parse<Kind>(parts[1]), new MemoryStream(b), parts[2]) : b;
    }
}
