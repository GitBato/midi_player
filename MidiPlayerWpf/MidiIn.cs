using System.Runtime.InteropServices;

namespace MidiPlayerWpf;

/// <summary>
/// 音源の返事（SysEx）を受け取るために、MIDI IN を全部開いて受け取ったバイトをためる。
/// コールバックは使わず、バッファの「受け取り済み」の印を見に行く（winmm の決まりで、コールバックの中で
/// winmm を呼んではいけないため）
/// </summary>
sealed class MidiInCapture : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct MidiHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public IntPtr lpNext;
        public IntPtr reserved;
        public uint dwOffset;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public IntPtr[] dwReserved;
    }

    [DllImport("winmm.dll")] static extern int midiInGetNumDevs();
    [DllImport("winmm.dll")] static extern int midiInOpen(out IntPtr h, int id, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int midiInClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInStart(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInStop(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInReset(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiInPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int midiInUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int midiInAddBuffer(IntPtr h, IntPtr hdr, int size);

    const uint MhdrDone = 1;
    const int BufSize = 1024, Bufs = 8;
    static readonly int HdrSize = Marshal.SizeOf<MidiHdr>();
    static readonly int FlagsAt = (int)Marshal.OffsetOf<MidiHdr>(nameof(MidiHdr.dwFlags));
    static readonly int RecordedAt = (int)Marshal.OffsetOf<MidiHdr>(nameof(MidiHdr.dwBytesRecorded));

    sealed class Device
    {
        public IntPtr Handle;
        public readonly List<(IntPtr hdr, IntPtr data)> Buffers = new();
    }

    readonly List<Device> _devs = new();

    /// <summary>開けた MIDI IN の数</summary>
    public int Count => _devs.Count;

    public static int DeviceCount => midiInGetNumDevs();

    /// <summary>MIDI IN を全部開いて受け取りを始める（使用中で開けないものは飛ばす）</summary>
    public MidiInCapture()
    {
        int n = midiInGetNumDevs();
        for (int id = 0; id < n; id++)
        {
            if (midiInOpen(out var h, id, IntPtr.Zero, IntPtr.Zero, 0) != 0) continue;
            var d = new Device { Handle = h };
            for (int i = 0; i < Bufs; i++)
            {
                var data = Marshal.AllocHGlobal(BufSize);
                var hdr = Marshal.AllocHGlobal(HdrSize);
                Marshal.StructureToPtr(new MidiHdr { lpData = data, dwBufferLength = BufSize, dwReserved = new IntPtr[8] }, hdr, false);
                midiInPrepareHeader(h, hdr, HdrSize);
                midiInAddBuffer(h, hdr, HdrSize);
                d.Buffers.Add((hdr, data));
            }
            midiInStart(h);
            _devs.Add(d);
        }
    }

    /// <summary>今までに受け取ったバイト（全部の MIDI IN の分）を取り出す</summary>
    public List<byte> Take()
    {
        var o = new List<byte>();
        foreach (var d in _devs)
        {
            foreach (var (hdr, data) in d.Buffers)
            {
                if (((uint)Marshal.ReadInt32(hdr, FlagsAt) & MhdrDone) == 0) continue;
                int n = Marshal.ReadInt32(hdr, RecordedAt);
                var b = new byte[Math.Clamp(n, 0, BufSize)];
                Marshal.Copy(data, b, 0, b.Length);
                o.AddRange(b);
                // バッファを戻して、また受け取れるようにする
                midiInUnprepareHeader(d.Handle, hdr, HdrSize);
                Marshal.StructureToPtr(new MidiHdr { lpData = data, dwBufferLength = BufSize, dwReserved = new IntPtr[8] }, hdr, false);
                midiInPrepareHeader(d.Handle, hdr, HdrSize);
                midiInAddBuffer(d.Handle, hdr, HdrSize);
            }
        }
        return o;
    }

    public void Dispose()
    {
        foreach (var d in _devs)
        {
            midiInStop(d.Handle);
            midiInReset(d.Handle); // 受け取り待ちのバッファを返してもらう
            foreach (var (hdr, data) in d.Buffers)
            {
                midiInUnprepareHeader(d.Handle, hdr, HdrSize);
                Marshal.FreeHGlobal(hdr);
                Marshal.FreeHGlobal(data);
            }
            midiInClose(d.Handle);
        }
        _devs.Clear();
    }
}
