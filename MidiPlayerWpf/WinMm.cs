using System.Runtime.InteropServices;

namespace MidiPlayerWpf;

/// <summary>Windows の MIDI OUT（winmm）をそのまま呼ぶ</summary>
static class WinMm
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MidiOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public ushort wTechnology;
        public ushort wVoices;
        public ushort wNotes;
        public ushort wChannelMask;
        public uint dwSupport;
    }

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

    const uint MhdrDone = 1;

    [DllImport("winmm.dll")] static extern int midiOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern int midiOutGetDevCapsW(UIntPtr id, ref MidiOutCaps caps, int size);
    [DllImport("winmm.dll")] static extern int midiOutOpen(out IntPtr h, int id, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] static extern int midiOutClose(IntPtr h);
    [DllImport("winmm.dll")] static extern int midiOutShortMsg(IntPtr h, uint msg);
    [DllImport("winmm.dll")] static extern int midiOutPrepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int midiOutUnprepareHeader(IntPtr h, IntPtr hdr, int size);
    [DllImport("winmm.dll")] static extern int midiOutLongMsg(IntPtr h, IntPtr hdr, int size);

    /// <summary>MIDI OUT の名前の一覧（番号順）</summary>
    public static List<string> OutputNames()
    {
        var list = new List<string>();
        int n = midiOutGetNumDevs();
        for (int i = 0; i < n; i++)
        {
            var caps = new MidiOutCaps();
            list.Add(midiOutGetDevCapsW((UIntPtr)i, ref caps, Marshal.SizeOf<MidiOutCaps>()) == 0 ? caps.szPname : $"MIDI OUT {i + 1}");
        }
        return list;
    }

    public static IntPtr Open(int id)
    {
        int r = midiOutOpen(out var h, id, IntPtr.Zero, IntPtr.Zero, 0);
        if (r != 0) throw new InvalidOperationException($"MIDI OUT を開けません（エラー {r}）");
        return h;
    }

    public static void Close(IntPtr h) => midiOutClose(h);

    /// <summary>1〜3 バイトのメッセージ、または SysEx を送る（SysEx は送り終わるまで待つ）</summary>
    public static void Send(IntPtr h, byte[] msg)
    {
        if (msg.Length == 0) return;
        if (msg[0] is 0xF0 or 0xF7)
        {
            SendLong(h, msg);
            return;
        }
        uint m = msg[0];
        if (msg.Length > 1) m |= (uint)msg[1] << 8;
        if (msg.Length > 2) m |= (uint)msg[2] << 16;
        midiOutShortMsg(h, m);
    }

    static void SendLong(IntPtr h, byte[] msg)
    {
        int size = Marshal.SizeOf<MidiHdr>();
        IntPtr data = Marshal.AllocHGlobal(msg.Length);
        IntPtr hdr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(msg, 0, data, msg.Length);
            var mh = new MidiHdr
            {
                lpData = data,
                dwBufferLength = (uint)msg.Length,
                dwBytesRecorded = (uint)msg.Length,
                dwReserved = new IntPtr[8],
            };
            Marshal.StructureToPtr(mh, hdr, false);
            if (midiOutPrepareHeader(h, hdr, size) != 0) return;
            if (midiOutLongMsg(h, hdr, size) == 0)
            {
                // 送り終わるまで待つ（4 KB で約 1.3 秒。念のため 10 秒で打ち切る）
                int flagsAt = (int)Marshal.OffsetOf<MidiHdr>(nameof(MidiHdr.dwFlags));
                var limit = Environment.TickCount64 + 10_000;
                while (((uint)Marshal.ReadInt32(hdr, flagsAt) & MhdrDone) == 0 && Environment.TickCount64 < limit) Thread.Sleep(1);
            }
            midiOutUnprepareHeader(h, hdr, size);
        }
        finally
        {
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
        }
    }
}
