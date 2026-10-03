using System;
using System.Runtime.InteropServices;

namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>
    /// winmm.dll을 직접 P/Invoke해서 시스템에 이미 등록된 MIDI 출력 장치
    /// (예: loopMIDI로 만든 가상 포트, 여기에 88emu나 MAME 빌드가 물려있다고 가정)
    /// 로 짧은 메시지/SysEx를 그대로 전달하는 얇은 래퍼.
    ///
    /// 주의: Windows에서 "새 가상 MIDI 포트"를 코드만으로 만드는 것은
    /// 커널 드라이버가 필요해서 일반 앱 코드로는 불가능합니다.
    /// 대신 loopMIDI(무료) 같은 도구로 가상 포트를 만들어두고,
    /// 이 클래스는 그 포트에 "연결"만 합니다.
    /// </summary>
    public sealed class RawMidiOut : IDisposable
    {
        [DllImport("winmm.dll")] private static extern int midiOutGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int midiOutGetDevCaps(IntPtr uDeviceID, ref MIDIOUTCAPS caps, int cbMidiOutCaps);
        [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr handle, int deviceID, IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr handle, int message);
        [DllImport("winmm.dll")] private static extern int midiOutPrepareHeader(IntPtr handle, ref MIDIHDR header, int sizeOfHeader);
        [DllImport("winmm.dll")] private static extern int midiOutUnprepareHeader(IntPtr handle, ref MIDIHDR header, int sizeOfHeader);
        [DllImport("winmm.dll")] private static extern int midiOutLongMsg(IntPtr handle, ref MIDIHDR header, int sizeOfHeader);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MIDIOUTCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public ushort wTechnology; public ushort wVoices, wNotes, wChannelMask; public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIDIHDR
        {
            public IntPtr lpData; public int dwBufferLength; public int dwBytesRecorded;
            public IntPtr dwUser; public int dwFlags; public IntPtr lpNext; public IntPtr reserved;
            public int dwOffset; public IntPtr dwReservedArray0, dwReservedArray1, dwReservedArray2, dwReservedArray3;
        }

        private IntPtr _handle = IntPtr.Zero;

        /// <summary>연결 가능한 MIDI 출력 장치 이름 목록 (loopMIDI 포트 포함).</summary>
        public static string[] ListDevices()
        {
            int count = midiOutGetNumDevs();
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                var caps = new MIDIOUTCAPS();
                midiOutGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<MIDIOUTCAPS>());
                names[i] = caps.szPname;
            }
            return names;
        }

        public void Open(int deviceIndex)
        {
            int rc = midiOutOpen(out _handle, deviceIndex, IntPtr.Zero, IntPtr.Zero, 0);
            if (rc != 0) throw new InvalidOperationException($"midiOutOpen 실패 (코드 {rc}) - 장치 인덱스 {deviceIndex}");
        }

        public void SendShort(byte status, byte data1, byte data2)
        {
            if (_handle == IntPtr.Zero) return;
            int packed = status | (data1 << 8) | (data2 << 16);
            midiOutShortMsg(_handle, packed);
        }

        public void SendSysEx(byte[] data)
        {
            if (_handle == IntPtr.Zero) return;
            var unmanaged = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, unmanaged, data.Length);
            var header = new MIDIHDR { lpData = unmanaged, dwBufferLength = data.Length };
            int headerSize = Marshal.SizeOf<MIDIHDR>();
            midiOutPrepareHeader(_handle, ref header, headerSize);
            midiOutLongMsg(_handle, ref header, headerSize);
            midiOutUnprepareHeader(_handle, ref header, headerSize);
            Marshal.FreeHGlobal(unmanaged);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) { midiOutClose(_handle); _handle = IntPtr.Zero; }
        }
    }
}
