using System.Collections.Concurrent;
using MidiCore;

namespace MidiPlayerWpf;

/// <summary>
/// GS・XG・MT-32・その他の 4 つの MIDI OUT。XG・MT-32・その他を選んでいないときは、その分も GS の出口へ送る。
/// 開く・閉じるなどの MIDI の呼び出しは、画面のスレッドではなく MIDI 専用のスレッドで行う
/// （画面のスレッドで MIDI を呼ぶと、ドラッグ＆ドロップが効かなくなることがあるため）。
/// </summary>
sealed class MidiOutputs : IMidiSink, IDisposable
{
    readonly object _lock = new();
    /// <summary>出口ごとの鍵（別々の出口へは同時に送れるように）</summary>
    readonly object[] _portLock = { new(), new(), new(), new() };
    readonly IntPtr[] _h = new IntPtr[4]; // Port の順（Main, Xg, Mt32, Other）
    readonly BlockingCollection<Action> _jobs = new();
    readonly Thread _thread;

    /// <summary>MT-32 宛ての短い SysEx の後に待つ時間（ms）</summary>
    public int Mt32ShortWaitMs = 10;

    public MidiOutputs()
    {
        _thread = new Thread(() =>
        {
            foreach (var job in _jobs.GetConsumingEnumerable()) job();
        }) { IsBackground = true, Name = "midi" };
        _thread.Start();
    }

    /// <summary>MIDI 専用のスレッドで実行して、終わるまで待つ</summary>
    public T Invoke<T>(Func<T> f)
    {
        if (Thread.CurrentThread == _thread) return f();
        var tcs = new TaskCompletionSource<T>();
        _jobs.Add(() =>
        {
            try { tcs.SetResult(f()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task.GetAwaiter().GetResult();
    }

    /// <summary>MIDI 専用のスレッドで実行する（待たない）</summary>
    public void Post(Action a) => _jobs.Add(a);

    public List<string> ListNames() => Invoke(WinMm.OutputNames);

    /// <summary>出口を開き直す。ids は Port の順の、MIDI OUT の番号（null は「GS と同じ」）。開けなかった理由を返す</summary>
    public List<string> Reopen(int?[] ids) => Invoke(() =>
    {
        var errors = new List<string>();
        lock (_lock)
        lock (_portLock[0]) lock (_portLock[1]) lock (_portLock[2]) lock (_portLock[3])
        {
            for (int i = 0; i < 4; i++)
            {
                if (_h[i] != IntPtr.Zero) WinMm.Close(_h[i]);
                _h[i] = IntPtr.Zero;
            }
            for (int i = 0; i < 4; i++)
            {
                if (ids[i] is not int id) continue;
                try { _h[i] = WinMm.Open(id); }
                catch (Exception e) { errors.Add($"{(Port)i}: {e.Message}"); }
            }
        }
        return errors;
    });

    /// <summary>その出口を開いているか（開いていなければ GS の出口へ送る）</summary>
    public bool IsOpen(Port p)
    {
        lock (_lock) return _h[(int)p] != IntPtr.Zero;
    }

    public void Send(byte[] msg, Port port)
    {
        // その出口を開いていなければ GS の出口へ
        int a = _h[(int)port] != IntPtr.Zero ? (int)port : (int)Port.Main;
        lock (_portLock[a])
        {
            var h = _h[a];
            if (h != IntPtr.Zero) WinMm.Send(h, msg);
        }
        // SysEx の後は音源の処理を待つ（リセット・大きい SysEx は 50ms、短いものは MT-32 宛てだけ）
        int wait = Routing.SysexWaitMs(msg, port, Mt32ShortWaitMs);
        if (wait > 0) Thread.Sleep(wait);
    }

    public void AllNotesOff() => Invoke(() =>
    {
        for (int a = 0; a < 4; a++)
        lock (_portLock[a])
        {
            var h = _h[a];
            {
                if (h == IntPtr.Zero) continue;
                for (int ch = 0; ch < 16; ch++)
                {
                    WinMm.Send(h, new byte[] { (byte)(0xB0 | ch), 64, 0 });
                    WinMm.Send(h, new byte[] { (byte)(0xB0 | ch), 123, 0 });
                    WinMm.Send(h, new byte[] { (byte)(0xB0 | ch), 120, 0 });
                }
            }
        }
        return 0;
    });

    /// <summary>
    /// MIDI OUT ごとに問い合わせを送り、MIDI IN に来た返事でつながっている音源を見分ける。
    /// 調べるあいだは出口を全部閉じる（終わったら Reopen で開き直す）。
    /// 返り値は MIDI OUT ごとの音源の種類（返事が無ければ null）と、開けた MIDI IN の数
    /// </summary>
    public (Port?[] kinds, int inputs) DetectModules(int count, Action<int> progress) => Invoke(() =>
    {
        lock (_lock)
        lock (_portLock[0]) lock (_portLock[1]) lock (_portLock[2]) lock (_portLock[3])
            for (int i = 0; i < 4; i++)
                if (_h[i] != IntPtr.Zero) { WinMm.Close(_h[i]); _h[i] = IntPtr.Zero; }
        var kinds = new Port?[count];
        using var cap = new MidiInCapture();
        if (cap.Count == 0) return (kinds, 0);
        for (int id = 0; id < count; id++)
        {
            progress(id);
            IntPtr h;
            try { h = WinMm.Open(id); }
            catch (Exception) { continue; }
            cap.Take(); // それまでに来ていたものは捨てる
            var got = new List<byte>();
            foreach (var probe in Detect.Probes)
            {
                WinMm.Send(h, probe);
                // DX7/TX7 のボイスダンプは 163 バイトあるので長めに待つ
                Thread.Sleep(probe.Length == 5 ? 500 : 300);
                got.AddRange(cap.Take());
            }
            WinMm.Close(h);
            kinds[id] = Detect.Classify(Detect.SplitSysex(got));
        }
        return (kinds, cap.Count);
    });

    public void Dispose()
    {
        Invoke(() =>
        {
            lock (_lock)
            lock (_portLock[0]) lock (_portLock[1]) lock (_portLock[2]) lock (_portLock[3])
                for (int i = 0; i < 4; i++)
                    if (_h[i] != IntPtr.Zero) { WinMm.Close(_h[i]); _h[i] = IntPtr.Zero; }
            return 0;
        });
        _jobs.CompleteAdding();
    }
}
