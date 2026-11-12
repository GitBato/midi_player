using System.IO.Compression;

namespace MidiCore;

/// <summary>
/// ZIP の中の曲を、展開せずにそのまま開く。古い日本の ZIP はファイル名が Shift_JIS なので、Shift_JIS として読む
/// （UTF-8 の印が付いた名前は UTF-8 のまま）。
/// </summary>
public sealed class Archive
{
    /// <summary>曲として開ける拡張子</summary>
    static readonly string[] SongExt = { "mid", "midi", "smf", "kar", "rmi", "rcp", "r36", "g36", "sng", "mm2", "mmc", "cm6", "dxx" };

    public sealed record Entry(string Name, string FullName);

    public string Path { get; }
    public List<Entry> Entries { get; }

    Archive(string path, List<Entry> entries) { Path = path; Entries = entries; }

    public static bool IsZip(string path) => string.Equals(System.IO.Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);

    static ZipArchive OpenZip(string path) =>
        new(File.OpenRead(path), ZipArchiveMode.Read, false, Text.ShiftJis);

    /// <summary>ZIP を開いて、曲として開けるファイルの一覧を作る（名前順）</summary>
    public static Archive Open(string path)
    {
        using var z = OpenZip(path);
        var list = new List<Entry>();
        foreach (var e in z.Entries)
        {
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
            var ext = System.IO.Path.GetExtension(e.FullName).TrimStart('.').ToLowerInvariant();
            if (SongExt.Contains(ext)) list.Add(new Entry(e.FullName, e.FullName));
        }
        if (list.Count == 0) throw new MidiFormatException("この ZIP には曲のファイルが入っていません");
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

    /// <summary>ZIP の中の 1 ファイルを読む</summary>
    public byte[] Read(Entry e)
    {
        using var z = OpenZip(Path);
        var entry = z.GetEntry(e.FullName) ?? throw new MidiFormatException($"{e.Name} が見つかりません");
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
