using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using MidiCore;

namespace MidiPlayerWpf;

public partial class MainWindow : Window
{
    readonly MidiOutputs _outs = new();
    readonly Sequencer _seq;
    readonly Settings _settings = Settings.Load();
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    /// <summary>メーター [Port × 16 + ch]</summary>
    readonly VuMeter[] _meters = new VuMeter[4 * 16];
    /// <summary>出口ごとのメーターの段</summary>
    readonly FrameworkElement[] _meterRows = new FrameworkElement[4];
    TimeSpan _lastRender;
    readonly CancellationTokenSource _quit = new();

    List<string> _names = new();
    /// <summary>選んでいる出口（Port の順。MIDI OUT の番号、null は未設定）</summary>
    readonly int?[] _ports = new int?[4];
    Port _songKind = Port.Main;
    Archive? _archive;
    bool _updating;
    bool _dragging;
    volatile bool _finished;
    /// <summary>TX7 へ DXX の音色を送っているあいだ true</summary>
    volatile bool _dxxSending;
    /// <summary>音源を調べているあいだ true（そのあいだは出口が閉じている）</summary>
    bool _detecting;
    volatile int _detectStep = -1;
    /// <summary>音源を調べ終わったら再生する</summary>
    bool _playAfterDetect;
    /// <summary>音源を調べているあいだに開いた曲（調べ終わってから開く。出口が閉じていて音色を送れないため）</summary>
    Action? _afterDetect;
    /// <summary>出口ごとに、自動判別で決まった MIDI OUT の番号と、どう見分けたか（光らせる用）</summary>
    readonly int?[] _detectedDev = new int?[4];
    readonly string[] _detectedHow = new string[4];

    static readonly Brush[] PortBrush =
    {
        new SolidColorBrush(Color.FromRgb(120, 220, 140)), // GS
        new SolidColorBrush(Color.FromRgb(200, 160, 250)), // XG
        new SolidColorBrush(Color.FromRgb(250, 180, 90)),  // MT-32
        new SolidColorBrush(Color.FromRgb(150, 210, 250)), // その他
    };

    public MainWindow()
    {
        InitializeComponent();
        _seq = new Sequencer(_outs);
        _seq.Finished += () => _finished = true;

        BuildMeters();
        _updating = true;
        foreach (var r in new[] { "自動", "GS", "XG", "MT-32" }) RouteCombo.Items.Add(r);
        RouteCombo.SelectedIndex = _settings.Route;
        _updating = false;
        RefreshPorts(_outs.ListNames(), true);
        // 起動したら、各 MIDI OUT の先の音源を調べて振り分ける
        Dispatcher.InvokeAsync(() => RunDetect(true), DispatcherPriority.Background);

        Drop += OnDrop;
        PreviewKeyDown += OnKey;
        Closing += (_, _) =>
        {
            _quit.Cancel();
            _timer.Stop();
            _seq.Stop();
            _outs.Dispose();
            _settings.Save();
        };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // MIDI OUT の抜き差しに追いつく（2 秒ごとに一覧を調べる。調べるのは MIDI 専用のスレッド）
        Task.Run(async () =>
        {
            while (!_quit.IsCancellationRequested)
            {
                try { await Task.Delay(2000, _quit.Token); } catch (TaskCanceledException) { return; }
                var names = _outs.ListNames();
                if (!names.SequenceEqual(_names) && !_detecting)
                    Dispatcher.Invoke(() =>
                    {
                        RefreshPorts(names, false);
                        // 抜き差しがあったら調べ直す（再生中はやめておく）
                        if (!_seq.IsPlaying) RunDetect(true);
                    });
            }
        });

        var arg = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault();
        if (arg != null) OpenPath(arg);
    }

    // ------------------------------------------------------------ 出口

    ComboBox[] PortCombos => new[] { GsCombo, XgCombo, Mt32Combo, OtherCombo };

    /// <summary>MIDI OUT の一覧が変わったら、覚えている名前で選び直して開き直す</summary>
    void RefreshPorts(List<string> names, bool first)
    {
        // 一覧が変わったら番号がずれるので、自動判別の印は消す
        if (!names.SequenceEqual(_names)) Array.Clear(_detectedDev);
        _names = names;
        for (int i = 0; i < 4; i++)
        {
            int idx = names.IndexOf(_settings.Ports[i]);
            _ports[i] = idx >= 0 ? idx : null;
        }
        // GS の出口が無ければ先頭
        if (_ports[0] == null && names.Count > 0 && (first || _settings.Ports[0] == "")) _ports[0] = 0;
        // 同じ MIDI OUT は 2 回開けないので、ほかと同じなら未設定にする
        for (int i = 1; i < 4; i++)
            for (int j = 0; j < i; j++)
                if (_ports[i] != null && _ports[i] == _ports[j]) _ports[i] = null;
        FillCombos();
        Reopen();
    }

    void FillCombos()
    {
        _updating = true;
        string[] label = { "GS", "XG", "MT-32", "その他" };
        for (int i = 0; i < 4; i++)
        {
            var combo = PortCombos[i];
            combo.Items.Clear();
            if (i > 0) combo.Items.Add(new ComboBoxItem { Content = "（GS と同じ）" });
            for (int n = 0; n < _names.Count; n++)
            {
                int user = Array.FindIndex(_ports, p => p == n);
                bool used = user >= 0 && user != i;
                combo.Items.Add(new ComboBoxItem
                {
                    Content = used ? $"{_names[n]}（{label[user]}で使用中）" : _names[n],
                    IsEnabled = !used,
                });
            }
            int sel = _ports[i] is int p ? p + (i > 0 ? 1 : 0) : (i > 0 ? 0 : -1);
            combo.SelectedIndex = sel;
            combo.ToolTip = i == 0 ? "GS / GM の曲を送る MIDI OUT（XG・MT-32・その他を選んでいなければ、それらもここへ送る）" : null;
        }
        _updating = false;
        UpdateGlow();
    }

    Border[] PortGlows => new[] { GsGlow, XgGlow, Mt32Glow, OtherGlow };

    /// <summary>自動判別で決まった出口を、出口の色の枠でずっと光らせる（手で選び直したら消える）</summary>
    void UpdateGlow()
    {
        string[] label = { "GS", "XG", "MT-32", "その他" };
        for (int p = 0; p < 4; p++)
        {
            var glow = PortGlows[p];
            bool lit = _detectedDev[p] is int dev && _ports[p] == dev;
            if (!lit)
            {
                glow.BorderBrush = Brushes.Transparent;
                glow.Background = null;
                glow.Effect = null;
                glow.ToolTip = null;
                continue;
            }
            glow.ToolTip = $"{label[p]} の音源を自動で見つけました（{_detectedHow[p]}）";
            if (glow.Effect is DropShadowEffect) continue; // もう光っている
            var color = ((SolidColorBrush)PortBrush[p]).Color;
            glow.BorderBrush = PortBrush[p];
            glow.Background = new SolidColorBrush(Color.FromArgb(0x60, color.R, color.G, color.B));
            var shadow = new DropShadowEffect { Color = color, ShadowDepth = 0, BlurRadius = 18, Opacity = 1 };
            // ゆっくり明滅させる（暗くなっても消えない）
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty,
                new DoubleAnimation(1.0, 0.45, TimeSpan.FromSeconds(1.2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
            glow.Effect = shadow;
        }
    }

    void Reopen()
    {
        var errors = _outs.Reopen(_ports);
        StatusText.Text = _names.Count == 0 ? "MIDI OUT が見つかりません" : string.Join("\n", errors);
    }

    void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        int i = Array.IndexOf(PortCombos, (ComboBox)sender);
        var combo = PortCombos[i];
        int idx = combo.SelectedIndex - (i > 0 ? 1 : 0);
        _ports[i] = idx >= 0 ? idx : null;
        _settings.Ports[i] = idx >= 0 ? _names[idx] : "";
        _settings.Save();
        bool was = _seq.IsPlaying;
        long pos = _seq.PositionUs;
        _seq.Stop();
        FillCombos();
        Reopen();
        if (was)
        {
            _seq.Seek(pos);
            _seq.Play();
        }
    }

    void RouteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _settings.Route = RouteCombo.SelectedIndex;
        _settings.Save();
        ApplyRoute();
    }

    /// <summary>曲の送り先を決める（自動なら開いた曲の種類で）</summary>
    void ApplyRoute()
    {
        var port = RouteCombo.SelectedIndex switch { 1 => Port.Main, 2 => Port.Xg, 3 => Port.Mt32, _ => _songKind };
        if (_seq.SongPort != port)
        {
            _outs.AllNotesOff();
            _seq.SongPort = port;
        }
    }

    // ------------------------------------------------------------ 曲を開く

    void OpenButton_Click(object sender, RoutedEventArgs e) => OpenDialog();

    void OpenDialog()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "曲 (*.mid;*.midi;*.rcp;*.r36;*.g36;*.sng;*.mm2;*.mmc;*.cm6;*.dxx;*.zip;*.lzh)|*.mid;*.midi;*.smf;*.kar;*.rmi;*.rcp;*.r36;*.g36;*.sng;*.mm2;*.mmc;*.cm6;*.dxx;*.zip;*.lzh;*.lha|すべて (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) == true) OpenPath(dlg.FileName);
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        // ドロップした曲はすぐ再生する
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenPath(files[0], true);
    }

    void OpenPath(string path, bool play = false)
    {
        try
        {
            if (Archive.IsArchive(path))
            {
                _archive = Archive.Open(path);
                _updating = true;
                ZipCombo.Items.Clear();
                foreach (var en in _archive.Entries) ZipCombo.Items.Add(en.Name);
                _updating = false;
                ZipRow.Visibility = Visibility.Visible;
                ZipCombo.SelectedIndex = -1;
                _updating = true;
                ZipCombo.SelectedIndex = 0;
                _updating = false;
                OpenEntry(0, play);
                return;
            }
            _archive = null;
            ZipRow.Visibility = Visibility.Collapsed;
            OpenBytes(Path.GetFileName(path), File.ReadAllBytes(path), path, play);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{Path.GetFileName(path)} を開けません: {ex.Message}";
        }
    }

    void ZipCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updating || _archive == null || ZipCombo.SelectedIndex < 0) return;
        OpenEntry(ZipCombo.SelectedIndex, _seq.IsPlaying);
    }

    void OpenEntry(int i, bool play)
    {
        if (_archive == null) return;
        var en = _archive.Entries[i];
        try
        {
            OpenBytes(en.Name, _archive.Read(en), null, play);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{en.Name} を開けません: {ex.Message}";
        }
    }

    /// <summary>曲を読み込む。path は本物のファイルのとき（同じフォルダの CM6 を探す）</summary>
    void OpenBytes(string name, byte[] b, string? path, bool play)
    {
        if (_detecting)
        {
            _afterDetect = () => OpenBytes(name, b, path, play);
            TitleText.Text = $"{name}（音源を調べ終わったら開きます）";
            return;
        }
        // TX7 の音色ファイルは、曲ではなく音色データとして送る
        if (Dxx.IsDxx(name, b))
        {
            SendDxx(name, b);
            return;
        }
        string? warning = null;
        Song song;
        try
        {
            if (Cm6.IsCm6(b)) song = new Song { TimbreSysex = Cm6.ToSysex(b) }; // CM6 だけなら音源設定を送るだけ
            else if (Rcp.IsRcp(b))
            {
                song = Rcp.Parse(b);
                // ヘッダで指定された CM6（無ければ同じ名前の .CM6）を、読み込み時に音源へ送る
                if (path != null && Cm6.FindFor(path, Rcp.Cm6Name(b)) is string cm6)
                {
                    try
                    {
                        song.TimbreSysex = Cm6.ToSysex(File.ReadAllBytes(cm6));
                        name += " + " + Path.GetFileName(cm6);
                    }
                    catch (Exception ex)
                    {
                        warning = $"{Path.GetFileName(cm6)} を送れません（音色なしで再生します）: {ex.Message}";
                    }
                }
            }
            else if (Sng.IsSng(b)) song = Sng.Parse(b);
            else if (Mm2.IsMm2(b)) song = Mm2.Parse(b);
            else if (Mmc.IsMmc(b)) song = Mmc.Parse(b);
            else song = Smf.Parse(b);
        }
        catch (MidiFormatException ex)
        {
            StatusText.Text = $"{name} を読み込めません: {ex.Message}";
            return;
        }
        _seq.Load(song);
        _songKind = Routing.SongKind(song);
        ApplyRoute();
        TitleText.Text = name;
        PosSlider.Maximum = Math.Max(song.LengthUs / 1e6, 0.001);
        PosSlider.Value = 0;
        StatusText.Text = warning ?? "";
        if (play) StartPlay();
    }

    /// <summary>TX7 / DX7 の音色ファイル（.DXX）を 32 音色バルクダンプで送る（受信チャンネル 1）</summary>
    void SendDxx(string name, byte[] b)
    {
        byte[] msg;
        try { msg = Dxx.ToSysex(b, 0); }
        catch (MidiFormatException ex)
        {
            StatusText.Text = $"{name} を送れません: {ex.Message}";
            return;
        }
        var voices = Dxx.VoiceNames(b);
        StatusText.Text = "";
        _dxxSending = true;
        Task.Run(() =>
        {
            try { _outs.Send(msg, Routing.PortFor(msg, Dest.Song, _seq.SongPort)); }
            finally { _dxxSending = false; }
            Dispatcher.Invoke(() => StatusText.Text = $"TX7 へ音色を送りました: {name}（{string.Join(" / ", voices.Take(8))} …）");
        });
    }

    // ------------------------------------------------------------ 再生

    void PlayButton_Click(object sender, RoutedEventArgs e) => TogglePlay();

    void TogglePlay()
    {
        if (_seq.IsPlaying) _seq.Stop();
        else StartPlay();
    }

    /// <summary>再生する（音源を調べているあいだは、終わってから）</summary>
    void StartPlay()
    {
        if (_detecting) _playAfterDetect = true;
        else _seq.Play();
    }

    // ------------------------------------------------------------ 音源を調べる

    void DetectButton_Click(object sender, RoutedEventArgs e) => RunDetect(false);

    /// <summary>各 MIDI OUT の先の音源を調べて、出口を振り分ける。auto は起動時・抜き差し時（静かに行う）</summary>
    void RunDetect(bool auto)
    {
        if (_detecting || _names.Count == 0) return;
        if (_seq.IsPlaying)
        {
            if (auto) return;
            _seq.Stop();
        }
        _detecting = true;
        DetectButton.IsEnabled = false;
        var names = _names.ToList();
        Task.Run(() =>
        {
            var (kinds, inputs) = _outs.DetectModules(names.Count, i => _detectStep = i);
            Dispatcher.Invoke(() => ApplyDetect(names, kinds, inputs, auto));
        });
    }

    void ApplyDetect(List<string> names, Port?[] kinds, int inputs, bool auto)
    {
        _detecting = false;
        _detectStep = -1;
        DetectButton.IsEnabled = true;
        string[] label = { "GS", "XG", "MT-32", "その他" };
        // 返事が無かったものは、MIDI OUT の名前（SC-88・MU・MT-32・TX7 など）から決める
        var replied = kinds.Select(k => k != null).ToArray();
        for (int i = 0; i < kinds.Length; i++)
            if (kinds[i] == null && MidiCore.Text.DestFromName(System.Text.Encoding.ASCII.GetBytes(names[i])) is Dest d)
                kinds[i] = d switch { Dest.Mt32 => Port.Mt32, Dest.Xg => Port.Xg, Dest.Other => Port.Other, _ => Port.Main };

        if (names.SequenceEqual(_names) && kinds.Any(k => k != null))
        {
            var found = new int?[4];
            for (int i = 0; i < kinds.Length; i++)
                if (kinds[i] is Port k && found[(int)k] == null) found[(int)k] = i;
            for (int p = 0; p < 4; p++)
            {
                if (found[p] is int id) _ports[p] = id;
                // 見つからなかった種類の出口が、別の音源の MIDI OUT を指していたら外す
                else if (_ports[p] is int cur && kinds[cur] != null) _ports[p] = null;
            }
            // GS が見つからなかったときは、どの音源とも分からなかった MIDI OUT を GS にする
            if (_ports[0] == null)
                _ports[0] = Enumerable.Range(0, kinds.Length).Cast<int?>().FirstOrDefault(i => kinds[i!.Value] == null && !_ports.Contains(i));
            for (int p = 0; p < 4; p++)
            {
                _settings.Ports[p] = _ports[p] is int id ? _names[id] : "";
                _detectedDev[p] = found[p];
                if (found[p] is int f) _detectedHow[p] = replied[f] ? "音源の返事で判別" : "MIDI OUT の名前で判別";
            }
            _settings.Save();
            FillCombos();
            var summary = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] != null).Select(i => $"{names[i]} → {label[(int)kinds[i]!.Value]}");
            Reopen();
            StatusText.Text = "音源を見分けました: " + string.Join("、", summary);
        }
        else
        {
            Reopen();
            if (!auto)
                StatusText.Text = inputs == 0
                    ? "MIDI IN が無いか使用中のため、音源を調べられませんでした（音源の MIDI OUT を PC の MIDI IN につないでください）"
                    : "どの音源からも返事がありませんでした（音源の MIDI OUT を PC の MIDI IN につないでください）";
        }
        var after = _afterDetect;
        _afterDetect = null;
        after?.Invoke();
        if (_playAfterDetect)
        {
            _playAfterDetect = false;
            _seq.Play();
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && e.OriginalSource is not TextBox)
        {
            TogglePlay();
            e.Handled = true;
        }
        else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
        {
            OpenDialog();
            e.Handled = true;
        }
    }

    void PosSlider_DragStarted(object sender, DragStartedEventArgs e) => _dragging = true;

    void PosSlider_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _dragging = false;
        _seq.Seek((long)(PosSlider.Value * 1e6));
    }

    void PosSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        // つまみ以外をクリックしたとき（IsMoveToPointEnabled でその位置へ）
        if (!_dragging) Dispatcher.InvokeAsync(() => _seq.Seek((long)(PosSlider.Value * 1e6)));
    }

    // ------------------------------------------------------------ 表示

    void BuildMeters()
    {
        string[] label = { "GS", "XG", "MT-32", "その他" };
        for (int p = 0; p < 4; p++)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var name = new TextBlock { Text = label[p], Width = 52, Foreground = PortBrush[p], FontWeight = FontWeights.SemiBold };
            DockPanel.SetDock(name, Dock.Left);
            row.Children.Add(name);
            var grid = new UniformGrid { Columns = 16, Rows = 1, Height = 58 };
            for (int ch = 0; ch < 16; ch++)
            {
                var m = new VuMeter { Channel = ch + 1, Accent = PortBrush[p], Margin = new Thickness(1) };
                _meters[p * 16 + ch] = m;
                grid.Children.Add(m);
            }
            row.Children.Add(grid);
            _meterRows[p] = row;
            MeterPanel.Children.Add(row);
        }
        // 針は画面の描き替えごとに動かす（なめらかに振れるように）
        CompositionTarget.Rendering += (_, e) =>
        {
            var t = ((RenderingEventArgs)e).RenderingTime;
            double dt = _lastRender == TimeSpan.Zero ? 0.016 : (t - _lastRender).TotalSeconds;
            _lastRender = t;
            if (dt <= 0) return;
            foreach (var m in _meters) m.Step(dt);
        };
    }

    void Tick()
    {
        // 最後まで行ったら：書庫なら次の曲へ
        if (_finished)
        {
            _finished = false;
            if (_archive != null && ZipCombo.SelectedIndex + 1 < ZipCombo.Items.Count)
            {
                _updating = true;
                ZipCombo.SelectedIndex++;
                _updating = false;
                var en = _archive.Entries[ZipCombo.SelectedIndex];
                try { OpenBytes(en.Name, _archive.Read(en), null, true); }
                catch (Exception ex) { StatusText.Text = $"{en.Name} を開けません: {ex.Message}"; }
            }
        }
        ShowTransfer();

        PlayButton.Content = _seq.IsPlaying ? "■" : "▶";
        long pos = _seq.PositionUs, len = _seq.Song?.LengthUs ?? 0;
        TimeText.Text = $"{Fmt(pos)} / {Fmt(len)}";
        if (!_dragging) PosSlider.Value = Math.Min(pos / 1e6, PosSlider.Maximum);

        // 開いている出口の段だけ出す（開いていない出口あての音は GS の出口へ行くので、GS の段に出す）
        var open = new bool[4];
        for (int p = 0; p < 4; p++)
        {
            open[p] = p == 0 || _outs.IsOpen((Port)p);
            var vis = open[p] ? Visibility.Visible : Visibility.Collapsed;
            if (_meterRows[p].Visibility != vis) _meterRows[p].Visibility = vis;
        }
        for (int ch = 0; ch < 16; ch++)
        {
            var target = new double[4];
            for (int p = 0; p < 4; p++)
            {
                int k = p * 16 + ch;
                float v = Math.Clamp(_seq.Levels[k], 0f, 1f);
                _seq.Levels[k] = v * 0.93f; // 音が消えたあと、ゆっくり下げる
                int shown = open[p] ? p : 0;
                // 小さい音でも針が動くよう、少し持ち上げる
                target[shown] = Math.Max(target[shown], Math.Pow(v, 0.6));
            }
            for (int p = 0; p < 4; p++) _meters[p * 16 + ch].Target = target[p];
        }
    }

    /// <summary>音色の SysEx を転送しているあいだ、帯と進み具合を出す</summary>
    void ShowTransfer()
    {
        bool setup = !_seq.SetupDone;
        bool show = setup || _dxxSending || _detecting;
        TransferBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show) return;
        if (_detecting)
        {
            int i = _detectStep;
            string name = i >= 0 && i < _names.Count ? _names[i] : "";
            TransferText.Text = $"◆ 音源を調べています… {Math.Max(i + 1, 0)} / {_names.Count}  {name}";
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = (double)Math.Max(i, 0) / Math.Max(_names.Count, 1);
        }
        else if (setup)
        {
            int sent = _seq.SetupSent, total = Math.Max(_seq.SetupTotal, 1);
            TransferText.Text = $"♪ MT-32 へ音色を転送中… {sent} / {total}（終わってから再生します）";
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = (double)sent / total;
        }
        else
        {
            TransferText.Text = "♪ TX7 へ音色（32 音色）を転送中…";
            TransferProgress.IsIndeterminate = true;
        }
    }

    static string Fmt(long us)
    {
        long s = us / 1_000_000;
        return $"{s / 60:00}:{s % 60:00}";
    }
}
