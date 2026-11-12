using System.Collections.Concurrent;
using System.Diagnostics;

namespace MidiCore;

/// <summary>MIDI を送る先（実際の MIDI OUT。テストでは記録するだけのもの）</summary>
public interface IMidiSink
{
    /// <summary>msg を port の音源へ送る（送り終わるまで、SysEx は音源の処理待ちまで戻らない）</summary>
    void Send(byte[] msg, Port port);
    /// <summary>開いている出口すべての音を止める</summary>
    void AllNotesOff();
}

/// <summary>
/// 曲を別スレッドで再生する。時刻どおりにイベントを送り、途中から再生するときは音色やコントロールを送り直す（チェイス）。
/// SysEx の処理待ちで遅れたら、その分だけ曲の時計を止める（溜まった音符を一気に出さない）。
/// </summary>
public sealed class Sequencer
{
    readonly IMidiSink _sink;
    Thread? _thread;
    volatile bool _stop;
    long _posUs;
    Task _setup = Task.CompletedTask;

    public Song? Song { get; private set; }
    /// <summary>曲の送り先（自動なら曲の種類から決めたもの）</summary>
    public volatile Port SongPort = Port.Main;
    /// <summary>出口・チャンネルごとの音の大きさ（0〜1。[Port × 16 + ch]。ノートオンで上がる。画面側で減らしていく）</summary>
    public readonly float[] Levels = new float[4 * 16];
    /// <summary>チャンネルごとに、最後に送った出口（まだ送っていなければ null）</summary>
    public readonly Port?[] ChannelPorts = new Port?[16];
    readonly int[] _volume = Enumerable.Repeat(100, 4 * 16).ToArray();

    public bool IsPlaying { get; private set; }
    public long PositionUs => Interlocked.Read(ref _posUs);
    /// <summary>曲が最後まで行って止まったとき（再生スレッドから呼ばれる）</summary>
    public event Action? Finished;

    /// <summary>
    /// 出口ごとの送信係。出口ごとに別のスレッドで送るので、ある音源への送信（MIDI ケーブルの速さや SysEx の処理待ち）が
    /// ほかの出口や曲の時計を止めない。ノートが混んでも曲の時計は進み続け、遅れた分はその出口だけで追いつく
    /// </summary>
    sealed class PortWorker
    {
        readonly BlockingCollection<(byte[] msg, int gen)> _q = new();
        readonly Sequencer _owner;
        readonly Port _port;
        int _busy;

        public PortWorker(Sequencer owner, Port port)
        {
            _owner = owner;
            _port = port;
            new Thread(Loop) { IsBackground = true, Name = $"midi-{port}", Priority = ThreadPriority.AboveNormal }.Start();
        }

        public void Add(byte[] msg)
        {
            Interlocked.Increment(ref _busy);
            _q.Add((msg, _owner._gen));
        }

        /// <summary>まだ送っていないものがあるか</summary>
        public bool Busy => Volatile.Read(ref _busy) > 0;

        void Loop()
        {
            foreach (var (msg, gen) in _q.GetConsumingEnumerable())
            {
                try
                {
                    // 止めたあと（世代が変わったあと）の残りは送らない
                    if (gen == _owner._gen) _owner._sink.Send(msg, _port);
                }
                catch (Exception) { }
                finally { Interlocked.Decrement(ref _busy); }
            }
        }
    }

    readonly PortWorker[] _workers;
    /// <summary>止めるたびに増やす（送信係に残っている古いメッセージを捨てるため）</summary>
    volatile int _gen;

    public Sequencer(IMidiSink sink)
    {
        _sink = sink;
        _workers = Enumerable.Range(0, 4).Select(p => new PortWorker(this, (Port)p)).ToArray();
    }

    /// <summary>全部の出口の送信係が送り終わるまで待つ（最大 timeoutMs）</summary>
    void Drain(int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (_workers.Any(w => w.Busy) && sw.ElapsedMilliseconds < timeoutMs && !_stop) Thread.Sleep(2);
    }

    /// <summary>曲を読み込む（再生中なら止める）。曲の音色データ（SNG・CM6）は別スレッドで送り始め、再生はその後になる</summary>
    public void Load(Song song)
    {
        Stop();
        Song = song;
        Interlocked.Exchange(ref _posUs, 0);
        Array.Clear(Levels);
        Array.Clear(ChannelPorts);
        var timbres = song.TimbreSysex.ToList();
        if (timbres.Count == 0) return;
        var prev = _setup;
        Volatile.Write(ref _setupSent, 0);
        Volatile.Write(ref _setupTotal, timbres.Count + 1);
        _setup = Task.Run(() =>
        {
            prev.Wait();
            // 転送の前に MT-32 を全リセットし、100ms 待つ
            _sink.Send(Routing.Mt32Reset, Port.Mt32);
            Thread.Sleep(50);
            Interlocked.Increment(ref _setupSent);
            foreach (var t in timbres)
            {
                _sink.Send(t, Routing.PortFor(t, Dest.Song, SongPort));
                Interlocked.Increment(ref _setupSent);
            }
        });
    }

    /// <summary>音色データの転送などが終わっているか</summary>
    public bool SetupDone => _setup.IsCompleted;
    /// <summary>音色データの転送の進み具合（送った数, 全部の数）</summary>
    public int SetupSent => Volatile.Read(ref _setupSent);
    public int SetupTotal => Volatile.Read(ref _setupTotal);
    int _setupSent, _setupTotal;

    public void Play()
    {
        var song = Song;
        if (song == null || IsPlaying) return;
        long from = PositionUs;
        if (from >= song.LengthUs) from = 0;
        _stop = false;
        IsPlaying = true;
        _thread = new Thread(() => Run(song, from)) { IsBackground = true, Name = "sequencer", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>止める（位置はそのまま）</summary>
    public void Stop()
    {
        _stop = true;
        var t = _thread;
        if (t != null && t != Thread.CurrentThread) t.Join();
        _thread = null;
        // 送信係に残っているものは捨てる
        _gen++;
        if (IsPlaying)
        {
            IsPlaying = false;
            _sink.AllNotesOff();
        }
        Array.Clear(Levels);
    }

    /// <summary>位置を変える。再生中なら新しい位置から再生し直す</summary>
    public void Seek(long us)
    {
        bool was = IsPlaying;
        Stop();
        Interlocked.Exchange(ref _posUs, Math.Max(us, 0));
        if (was) Play();
    }

    Port PortOf(TimedEvent e) => Routing.PortFor(e.Data, e.Dest, SongPort);

    void Emit(byte[] msg, Port port)
    {
        _workers[(int)port].Add(msg);
        int st = msg[0] & 0xF0, ch = msg[0] & 0x0F;
        if (msg[0] < 0xF0) ChannelPorts[ch] = port;
        int k = (int)port * 16 + ch;
        if (st == 0x90 && msg.Length >= 3 && msg[2] > 0)
            Levels[k] = Math.Max(Levels[k], msg[2] / 127f * _volume[k] / 127f);
        else if (st == 0xB0 && msg.Length >= 3 && msg[1] == 7)
            _volume[k] = msg[2];
    }

    /// <summary>途中から再生するとき、それまでの音色・コントロール・SysEx を送り直す（ノートは送らない）</summary>
    void Chase(Song song, long untilUs)
    {
        var bend = new (byte, byte)?[16];
        var cc = new byte?[16 * 128];
        var chPort = new Port[16];
        for (int c = 0; c < 16; c++) chPort[c] = SongPort;
        foreach (var ev in song.Events)
        {
            if (ev.TimeUs >= untilUs || _stop) break;
            var d = ev.Data;
            int ch = d[0] & 0x0F;
            var port = PortOf(ev);
            if (d[0] < 0xF0) chPort[ch] = port;
            if (d[0] is 0xF0 or 0xF7)
            {
                // リセットなどの処理待ちは送信係が行う
                Emit(d, port);
            }
            else if ((d[0] & 0xF0) == 0xC0)
            {
                // バンクセレクトの後に音色を選ぶ必要があるので、ここで確定させる
                if (cc[ch * 128] is byte msb)
                {
                    Emit(new byte[] { (byte)(0xB0 | ch), 0, msb }, port);
                    if (cc[ch * 128 + 32] is byte lsb) Emit(new byte[] { (byte)(0xB0 | ch), 32, lsb }, port);
                }
                Emit(new byte[] { (byte)(0xC0 | ch), d[1] }, port);
            }
            else if ((d[0] & 0xF0) == 0xB0 && d.Length >= 3)
            {
                // RPN/NRPN のデータ入力は順番が大事なのでその場で送る
                if (d[1] is 6 or 38 or 96 or 97 or 98 or 99 or 100 or 101) Emit(d, port);
                else if (d[1] < 120) cc[ch * 128 + d[1]] = d[2];
            }
            else if ((d[0] & 0xF0) == 0xE0 && d.Length >= 3) bend[ch] = (d[1], d[2]);
        }
        for (int ch = 0; ch < 16; ch++)
        {
            for (int n = 1; n < 120; n++)
            {
                if (n == 32) continue;
                if (cc[ch * 128 + n] is byte v) Emit(new byte[] { (byte)(0xB0 | ch), (byte)n, v }, chPort[ch]);
            }
            if (bend[ch] is (byte l, byte m)) Emit(new byte[] { (byte)(0xE0 | ch), l, m }, chPort[ch]);
        }
    }

    void Run(Song song, long fromUs)
    {
        try
        {
            // 音色データの転送が終わるまで待つ
            while (!_setup.IsCompleted)
            {
                if (_stop) return;
                Thread.Sleep(10);
            }
            Array.Fill(_volume, 100);
            if (fromUs > 0)
            {
                Chase(song, fromUs);
                // 送り直したもの（リセットの処理待ちを含む）が全部届いてから時計を動かす
                Drain(10_000);
                if (_stop) return;
            }
            var events = song.Events;
            int idx = LowerBound(events, fromUs);
            double baseSong = fromUs;
            var clock = Stopwatch.StartNew();
            double baseTime = 0; // clock の μs
            double NowUs() => clock.Elapsed.TotalMilliseconds * 1000.0;

            while (!_stop)
            {
                double songNow = baseSong + (NowUs() - baseTime);
                while (idx < events.Count && !_stop)
                {
                    var ev = events[idx];
                    if (ev.TimeUs > songNow) break;
                    var port = PortOf(ev);
                    Emit(ev.Data, port);
                    idx++;
                    // リセットや大きい SysEx（音源の処理に 50ms 待つもの）のときだけ、その分曲の時計を止める。
                    // 止めないと、処理待ちのあいだにたまった音符がそのあと一気に出てしまう。
                    // 短い SysEx やノートが混んだときは止めない（テンポが遅くならないように）
                    int wait = Routing.SysexWaitMs(ev.Data, port);
                    if (wait >= 50)
                    {
                        baseTime += wait * 1000.0 + ev.Data.Length * 320.0; // 処理待ち＋ケーブルを流れる時間
                        break;
                    }
                }
                Interlocked.Exchange(ref _posUs, (long)Math.Min(songNow, song.LengthUs));
                if (idx >= events.Count && songNow >= song.LengthUs)
                {
                    Interlocked.Exchange(ref _posUs, song.LengthUs);
                    Drain(3000);
                    _sink.AllNotesOff();
                    IsPlaying = false;
                    Finished?.Invoke();
                    return;
                }
                // 次のイベントまで待つ（停止にすぐ反応できるよう最大 2ms）
                double waitUs = idx < events.Count ? Math.Max(events[idx].TimeUs - songNow, 0) : 2000;
                if (waitUs > 1500) Thread.Sleep(1);
                else if (waitUs > 200) Thread.Yield();
            }
        }
        catch (Exception)
        {
            IsPlaying = false;
        }
    }

    static int LowerBound(List<TimedEvent> ev, long us)
    {
        int lo = 0, hi = ev.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (ev[mid].TimeUs < us) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
