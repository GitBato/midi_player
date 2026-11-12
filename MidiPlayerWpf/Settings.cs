using System.IO;

namespace MidiPlayerWpf;

/// <summary>前回選んだ出口（名前で覚える）と曲の送り先。%APPDATA%\midi_player_wpf\ports.txt</summary>
sealed class Settings
{
    /// <summary>GS・XG・MT-32・その他の出口の名前（空なら未設定）</summary>
    public string[] Ports = { "", "", "", "" };
    /// <summary>曲の送り先（0 = 自動、1 = GS、2 = XG、3 = MT-32）</summary>
    public int Route;

    static readonly string[] Keys = { "gs", "xg", "mt32", "other" };

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "midi_player_wpf", "ports.txt");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            if (!File.Exists(FilePath)) return s;
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                var (k, v) = (line[..eq], line[(eq + 1)..]);
                int i = Array.IndexOf(Keys, k);
                if (i >= 0) s.Ports[i] = v;
                else if (k == "route" && int.TryParse(v, out var r)) s.Route = Math.Clamp(r, 0, 3);
            }
        }
        catch (Exception) { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var lines = Keys.Select((k, i) => $"{k}={Ports[i]}").Append($"route={Route}");
            File.WriteAllLines(FilePath, lines);
        }
        catch (Exception) { }
    }
}
