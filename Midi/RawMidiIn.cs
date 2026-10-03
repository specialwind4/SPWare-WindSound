using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>
    /// winmm.dll MIDI 입력 래퍼. loopMIDI 등으로 만든 가상 포트를 "입력"으로 열어서,
    /// 들어오는 메시지를 MidiRouter로 넘깁니다.
    ///
    /// 중요 - 왜 전용 스레드를 쓰는가:
    /// winmm의 MIDI 콜백은 드라이버 컨텍스트에서 호출되고, 공식 문서가 "이 콜백 안에서
    /// 멀티미디어 함수를 호출하면 데드락이 발생할 수 있다"고 못박고 있습니다.
    /// 처음엔 콜백 안에서 바로 midiInAddBuffer를 부르고 엔진까지 돌렸는데, 그러면
    /// 재생을 시작하는 순간 앱이 죽습니다(실제로 그랬습니다). 그래서 지금은
    ///   콜백  : 데이터만 큐에 복사하고 즉시 반환 (winmm 함수 호출 없음)
    ///   워커  : 큐에서 꺼내 이벤트를 올리고, 버퍼 재등록도 여기서 수행
    /// 로 분리했습니다. 엔진 렌더링/에뮬레이션 같은 무거운 작업도 전부 워커 쪽에서
    /// 일어나므로 드라이버를 붙잡지 않습니다.
    /// </summary>
    public sealed class RawMidiIn : IDisposable
    {
        private delegate void MidiInProc(IntPtr handle, int msg, IntPtr instance, IntPtr param1, IntPtr param2);

        [DllImport("winmm.dll")] private static extern int midiInGetNumDevs();
        [DllImport("winmm.dll", CharSet = CharSet.Auto)]
        private static extern int midiInGetDevCaps(IntPtr uDeviceID, ref MIDIINCAPS caps, int cbMidiInCaps);
        [DllImport("winmm.dll")] private static extern int midiInOpen(out IntPtr handle, int deviceID, MidiInProc callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] private static extern int midiInStart(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int midiInStop(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int midiInReset(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int midiInClose(IntPtr handle);
        [DllImport("winmm.dll")] private static extern int midiInPrepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int midiInUnprepareHeader(IntPtr handle, IntPtr header, int size);
        [DllImport("winmm.dll")] private static extern int midiInAddBuffer(IntPtr handle, IntPtr header, int size);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct MIDIINCAPS
        {
            public ushort wMid, wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MIDIHDR
        {
            public IntPtr lpData;
            public int dwBufferLength;
            public int dwBytesRecorded;
            public IntPtr dwUser;
            public int dwFlags;
            public IntPtr lpNext;
            public IntPtr reserved;
            public int dwOffset;
            public IntPtr r0, r1, r2, r3;
        }

        private const int MIM_DATA = 0x3C3;
        private const int MIM_LONGDATA = 0x3C4;
        private const int CALLBACK_FUNCTION = 0x30000;

        private const int SysExBufferCount = 4;
        private const int SysExBufferSize = 65536;

        /// <summary>큐에 들어가는 항목. 짧은 메시지이거나 SysEx 덩어리입니다.</summary>
        private readonly struct Item
        {
            public readonly bool IsSysEx;
            public readonly byte Status, Data1, Data2;
            public readonly byte[]? SysEx;
            public readonly IntPtr HeaderToRequeue;

            public Item(byte status, byte d1, byte d2)
            {
                IsSysEx = false; Status = status; Data1 = d1; Data2 = d2;
                SysEx = null; HeaderToRequeue = IntPtr.Zero;
            }

            public Item(byte[]? sysex, IntPtr header)
            {
                IsSysEx = true; Status = Data1 = Data2 = 0;
                SysEx = sysex; HeaderToRequeue = header;
            }
        }

        private IntPtr _handle = IntPtr.Zero;
        private MidiInProc? _callback; // GC 방지용으로 필드에 보관
        private readonly List<IntPtr> _headers = new();
        private readonly int _headerSize = Marshal.SizeOf<MIDIHDR>();

        private readonly BlockingCollection<Item> _queue =
            new(new ConcurrentQueue<Item>(), boundedCapacity: 4096);
        private Thread? _worker;
        private volatile bool _closing;

        public event EventHandler<(byte status, byte data1, byte data2)>? ShortMessageReceived;
        public event EventHandler<byte[]>? SysExReceived;

        public static string[] ListDevices()
        {
            int count = midiInGetNumDevs();
            var names = new string[count];
            for (int i = 0; i < count; i++)
            {
                var caps = new MIDIINCAPS();
                midiInGetDevCaps((IntPtr)i, ref caps, Marshal.SizeOf<MIDIINCAPS>());
                names[i] = caps.szPname;
            }
            return names;
        }

        public void Open(int deviceIndex)
        {
            _callback = OnMidiMessage;
            int rc = midiInOpen(out _handle, deviceIndex, _callback, IntPtr.Zero, CALLBACK_FUNCTION);
            if (rc != 0)
            {
                _handle = IntPtr.Zero;
                throw new InvalidOperationException($"midiInOpen 실패 (코드 {rc}) - 장치 인덱스 {deviceIndex}");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "SPWare MIDI In Dispatcher",
                Priority = ThreadPriority.AboveNormal,
            };
            _worker.Start();

            AllocateSysExBuffers();
            midiInStart(_handle);
        }

        private void AllocateSysExBuffers()
        {
            for (int i = 0; i < SysExBufferCount; i++)
            {
                IntPtr data = Marshal.AllocHGlobal(SysExBufferSize);
                IntPtr header = Marshal.AllocHGlobal(_headerSize);

                var hdr = new MIDIHDR
                {
                    lpData = data,
                    dwBufferLength = SysExBufferSize,
                    dwBytesRecorded = 0,
                    dwFlags = 0,
                };
                Marshal.StructureToPtr(hdr, header, false);

                if (midiInPrepareHeader(_handle, header, _headerSize) == 0 &&
                    midiInAddBuffer(_handle, header, _headerSize) == 0)
                {
                    _headers.Add(header);
                }
                else
                {
                    Marshal.FreeHGlobal(header);
                    Marshal.FreeHGlobal(data);
                }
            }
        }

        /// <summary>
        /// 드라이버 콜백. 여기서는 절대 winmm 함수를 부르지 않고, 이벤트도 올리지 않습니다.
        /// 데이터만 복사해서 큐에 넣고 즉시 반환합니다.
        /// </summary>
        private void OnMidiMessage(IntPtr handle, int msg, IntPtr instance, IntPtr param1, IntPtr param2)
        {
            if (_closing) return;

            try
            {
                switch (msg)
                {
                    case MIM_DATA:
                    {
                        int packed = (int)param1;
                        _queue.TryAdd(new Item(
                            (byte)(packed & 0xFF),
                            (byte)((packed >> 8) & 0xFF),
                            (byte)((packed >> 16) & 0xFF)));
                        break;
                    }

                    case MIM_LONGDATA:
                    {
                        IntPtr headerPtr = param1;
                        var hdr = Marshal.PtrToStructure<MIDIHDR>(headerPtr);

                        byte[]? data = null;
                        if (hdr.dwBytesRecorded > 0)
                        {
                            data = new byte[hdr.dwBytesRecorded];
                            Marshal.Copy(hdr.lpData, data, 0, hdr.dwBytesRecorded);
                        }

                        // 버퍼 재등록은 워커 스레드에서 합니다(콜백 안에서 부르면 위험).
                        if (!_queue.TryAdd(new Item(data, headerPtr)))
                        {
                            // 큐가 꽉 찼으면 최소한 버퍼는 잃지 않도록 표시만 해둡니다.
                            // (다음 Dispose에서 정리됩니다)
                        }
                        break;
                    }
                }
            }
            catch
            {
                // 콜백에서 예외가 드라이버로 넘어가면 프로세스가 죽습니다. 반드시 삼킵니다.
            }
        }

        private void WorkerLoop()
        {
            try
            {
                foreach (var item in _queue.GetConsumingEnumerable())
                {
                    if (_closing) break;

                    try
                    {
                        if (!item.IsSysEx)
                        {
                            ShortMessageReceived?.Invoke(this, (item.Status, item.Data1, item.Data2));
                            continue;
                        }

                        if (item.SysEx is { Length: > 0 })
                            SysExReceived?.Invoke(this, item.SysEx);

                        // 버퍼를 드라이버에 돌려줍니다 (여기는 콜백 밖이라 안전)
                        if (item.HeaderToRequeue != IntPtr.Zero && !_closing)
                        {
                            var hdr = Marshal.PtrToStructure<MIDIHDR>(item.HeaderToRequeue);
                            hdr.dwBytesRecorded = 0;
                            Marshal.StructureToPtr(hdr, item.HeaderToRequeue, false);
                            midiInAddBuffer(_handle, item.HeaderToRequeue, _headerSize);
                        }
                    }
                    catch
                    {
                        // 한 메시지 처리가 실패해도 입력 전체가 멈추면 안 됩니다.
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding 이후 정상 종료
            }
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;

            _closing = true;

            midiInStop(_handle);
            midiInReset(_handle); // 대기 중인 버퍼를 모두 돌려받습니다

            _queue.CompleteAdding();
            _worker?.Join(1000);
            _worker = null;

            foreach (var header in _headers)
            {
                midiInUnprepareHeader(_handle, header, _headerSize);
                var hdr = Marshal.PtrToStructure<MIDIHDR>(header);
                if (hdr.lpData != IntPtr.Zero) Marshal.FreeHGlobal(hdr.lpData);
                Marshal.FreeHGlobal(header);
            }
            _headers.Clear();

            midiInClose(_handle);
            _handle = IntPtr.Zero;

            _queue.Dispose();
        }
    }
}
