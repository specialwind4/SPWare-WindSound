using System;
using System.Collections.Generic;
using System.Linq;
using SPWare.VirtualSoundCanvas.Engines;

namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>전역(장치 전체) SysEx를 어느 엔진에 보낼지 정하는 정책.</summary>
    public enum GlobalSysExPolicy
    {
        /// <summary>현재 "활성" 엔진(사용자가 선택한 기본 엔진)에만 보낸다.</summary>
        ActiveEngineOnly,
        /// <summary>등록된 모든 엔진에 똑같이 방송한다.</summary>
        BroadcastToAll,
    }

    /// <summary>
    /// 가상 MIDI 입력 하나를 받아, 채널 단위로 서로 다른 ISynthEngine에
    /// 분배하는 라우터. SPE3/SC-88Pro 두 "인격"이 한 포트 안에 공존하는
    /// 핵심 로직이 여기 있습니다.
    /// </summary>
    public class MidiRouter
    {
        // MIDI 스레드는 잠금 없이 읽고, 배정을 바꿀 때는 통째로 교체(복사 후 교체)한다.
        // 자동 감지가 MIDI 스레드에서 배정을 바꾸고, 화면(UI 스레드)에서도 바꾸기 때문에
        // 예전처럼 Dictionary 하나를 두 스레드가 동시에 만지면 안 된다.
        private volatile Dictionary<int, ISynthEngine> _channelMap = new();
        private readonly object _mapLock = new();
        private readonly List<ISynthEngine> _engines = new();

        public ISynthEngine? ActiveEngine { get; set; }
        public GlobalSysExPolicy GlobalPolicy { get; set; } = GlobalSysExPolicy.ActiveEngineOnly;

        /// <summary>채널(0-15)에 노트온이 들어올 때마다 (채널, velocity/127, 그 노트가 실제로 가는 엔진) 로 발생 - 레벨 미터 등에 사용.
        /// 패널은 자기 엔진의 이벤트만 걸러서 표시해야 합니다(다른 엔진으로 가는 노트에는 반응하면 안 됨).</summary>
        public event EventHandler<(int channel, double level, ISynthEngine engine)>? ChannelActivity;

        /// <summary>채널별로 지나가는 Level/Pan/Reverb/Chorus/Program 값이 바뀔 때마다 발생 - SC-88Pro류 정보 패널에 사용.</summary>
        public event EventHandler<int>? ChannelStateChanged; // 인자: 바뀐 채널 번호

        public readonly struct ChannelState
        {
            public int Program { get; init; }
            public int BankMsb { get; init; } // CC0, SC-88Pro에서는 이 값이 "배리에이션 번호" 역할
            public int Level { get; init; }   // CC7,  기본 100
            public int Pan { get; init; }     // CC10, 기본 64(중앙)
            public int Reverb { get; init; }  // CC91, 기본 40
            public int Chorus { get; init; }  // CC93, 기본 0
        }

        private readonly ChannelState[] _channelStates = CreateDefaultStates();

        // ---- 재생 중 엔진을 바꿀 때 새 엔진에 현재 곡의 설정을 다시 넣어 주기 위한 기록 ----
        // 곡의 초기 설정(SysEx, 음색/음량/팬/이펙트 값)은 곡 맨 앞에서 한 번만 오기 때문에, 재생 도중 MIDI 종류나 기본 엔진을 바꾸면
        // 새 엔진은 그걸 못 받아서 기본 음색(피아노)/기본 음량으로 울린다(빠진 소리). 다음 곡은 설정을 다시 보내니 정상이다.
        private readonly object _replayLock = new();
        // 기록은 "도착한 순서"(_seq)와 함께 남긴다. 다시 보낼 때 그 순서를 지켜야 한다: 게임이 프로그램 체인지를 보낸 "뒤에" 음색을
        // 임시 영역(04 xx xx)에 직접 올리는 경우, SysEx를 전부 먼저 보내고 프로그램 체인지를 나중에 보내면 프로그램 체인지가 올려 둔
        // 음색을 덮어써서 빈 소리가 난다(원숭이섬처럼 초기화 신호 없이 MT-32 SysEx만 쓰는 게임).
        private long _seq;
        private readonly List<(long seq, byte[] data)> _sysexJournal = new();
        private int _sysexJournalBytes;
        private const int SysexJournalMaxBytes = 256 * 1024;
        private readonly short[,] _ccLast = NewUnset(16, 128);
        private readonly long[,] _ccSeq = new long[16, 128];
        private readonly short[] _programLast = NewUnset(16);
        private readonly long[] _programSeq = new long[16];
        private readonly short[] _bendLast = NewUnset(16);
        private readonly long[] _bendSeq = new long[16];
        private static readonly int[] ReplayControllers = { 0, 32, 7, 10, 11, 71, 72, 73, 74, 91, 93 };

        private static short[] NewUnset(int n) { var a = new short[n]; Array.Fill(a, (short)-1); return a; }
        private static short[,] NewUnset(int a, int b)
        {
            var m = new short[a, b];
            for (int i = 0; i < a; i++) for (int j = 0; j < b; j++) m[i, j] = -1;
            return m;
        }

        private void ResetReplayState()
        {
            lock (_replayLock)
            {
                _sysexJournal.Clear(); _sysexJournalBytes = 0; _stateDirty = true;
                for (int c = 0; c < 16; c++)
                {
                    for (int k = 0; k < 128; k++) _ccLast[c, k] = -1;
                    _programLast[c] = -1; _bendLast[c] = -1;
                }
            }
        }

        private void Record(int channel, int controller, int value)
        {
            if (Array.IndexOf(ReplayControllers, controller) < 0) return;
            lock (_replayLock) { _ccLast[channel, controller] = (short)value; _ccSeq[channel, controller] = ++_seq; _stateDirty = true; }
        }

        // ---- 앱을 껐다 켜도 곡의 설정을 이어받기 위한 저장/복원 ----
        // 곡/게임이 켜져 있는 채로 앱만 바꾸면(예: 개인 버전 -> 공개 버전) 새 앱은 곡 앞부분에서 한 번 온 설정을 받은 적이 없어 빈 소리가 난다.
        private volatile bool _stateDirty;
        private MidiKind _restoredKind = MidiKind.Custom;
        private bool _restorePending;

        /// <summary>마지막 <see cref="ExportState"/> 뒤로 기록이 바뀌었는가.</summary>
        public bool StateDirty => _stateDirty;

        /// <summary>기록을 도착 순서대로 바이트열로 만든다. 기록이 없으면 null.</summary>
        public byte[]? ExportState()
        {
            var items = new List<(long seq, byte type, byte[] payload)>();
            MidiKind kind = CurrentKind != MidiKind.Custom ? CurrentKind : (ActiveEngine is { } ae ? KindOf(ae) : MidiKind.GmGs);
            lock (_replayLock)
            {
                _stateDirty = false;
                foreach (var (seq, d) in _sysexJournal) items.Add((seq, 0, d));
                for (int ch = 0; ch < 16; ch++)
                {
                    foreach (int k in ReplayControllers)
                        if (_ccLast[ch, k] >= 0) items.Add((_ccSeq[ch, k], 1, new[] { (byte)ch, (byte)k, (byte)_ccLast[ch, k] }));
                    if (_programLast[ch] >= 0) items.Add((_programSeq[ch], 2, new[] { (byte)ch, (byte)_programLast[ch] }));
                    if (_bendLast[ch] >= 0) items.Add((_bendSeq[ch], 3, new[] { (byte)ch, (byte)(_bendLast[ch] & 0x7F), (byte)(_bendLast[ch] >> 7) }));
                }
            }
            if (items.Count == 0) return null;
            items.Sort((a, b) => a.seq.CompareTo(b.seq));
            using var ms = new System.IO.MemoryStream();
            using (var w = new System.IO.BinaryWriter(ms))
            {
                w.Write(0x534D5053);                      // "SPMS"
                w.Write(1);                               // 형식 버전
                w.Write(DateTime.UtcNow.Ticks);
                w.Write((int)kind);
                w.Write(items.Count);
                foreach (var it in items) { w.Write(it.type); w.Write(it.payload.Length); w.Write(it.payload); }
            }
            return ms.ToArray();
        }

        /// <summary>
        /// 저장해 둔 기록을 불러온다(시작할 때 한 번). <paramref name="maxAge"/>보다 오래됐거나 형식이 안 맞으면 무시한다.
        /// 불러온 설정은 해당 종류의 엔진이 열릴 때 <see cref="RestoreTo"/>가 보낸다.
        /// </summary>
        public bool ImportState(byte[] data, TimeSpan maxAge)
        {
            try
            {
                using var r = new System.IO.BinaryReader(new System.IO.MemoryStream(data));
                if (r.ReadInt32() != 0x534D5053 || r.ReadInt32() != 1) return false;
                var saved = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
                if (DateTime.UtcNow - saved > maxAge || saved > DateTime.UtcNow.AddMinutes(5)) return false;
                int kind = r.ReadInt32();
                int count = r.ReadInt32();
                if (count < 0 || count > 20000) return false;
                var sx = new List<(long, byte[])>(); var cc = new List<(int, int, int, long)>(); var pr = new List<(int, int, long)>(); var pb = new List<(int, int, long)>();
                long seq = 0; int bytes = 0;
                for (int i = 0; i < count; i++)
                {
                    byte type = r.ReadByte(); int len = r.ReadInt32();
                    if (len < 0 || len > 65536) return false;
                    var p = r.ReadBytes(len); if (p.Length != len) return false;
                    seq++;
                    switch (type)
                    {
                        case 0: if (bytes + len > SysexJournalMaxBytes) return false; bytes += len; sx.Add((seq, p)); break;
                        case 1: if (len == 3 && p[0] < 16 && p[1] < 128) cc.Add((p[0], p[1], p[2], seq)); break;
                        case 2: if (len == 2 && p[0] < 16) pr.Add((p[0], p[1], seq)); break;
                        case 3: if (len == 3 && p[0] < 16) pb.Add((p[0], p[1] | (p[2] << 7), seq)); break;
                    }
                }
                ResetReplayState();
                lock (_replayLock)
                {
                    foreach (var (s, d) in sx) { _sysexJournal.Add((s, d)); _sysexJournalBytes += d.Length; }
                    foreach (var (ch, k, v, s) in cc) { _ccLast[ch, k] = (short)v; _ccSeq[ch, k] = s; }
                    foreach (var (ch, v, s) in pr) { _programLast[ch] = (short)v; _programSeq[ch] = s; }
                    foreach (var (ch, v, s) in pb) { _bendLast[ch] = (short)v; _bendSeq[ch] = s; }
                    _seq = seq; _stateDirty = false;
                }
                _restoredKind = Enum.IsDefined(typeof(MidiKind), kind) ? (MidiKind)kind : MidiKind.Custom;
                _restorePending = true;
                return true;
            }
            catch { return false; }
        }

        /// <summary>엔진이 열린 직후 호출. 불러온 설정의 종류가 이 엔진의 종류와 같으면 그 설정을 이 엔진에 보낸다(한 번만).</summary>
        public bool RestoreTo(ISynthEngine engine)
        {
            if (!_restorePending) return false;
            var target = _restoredKind != MidiKind.Custom ? KindToEngine?.Invoke(_restoredKind) : ActiveEngine;
            if (!ReferenceEquals(target, engine)) return false;
            _restorePending = false;
            ReplayStateTo(engine, KindOf(engine));
            return true;
        }

        /// <summary>
        /// 지금까지 곡에서 받은 설정(SysEx 기록 + 채널별 음색/음량/팬/이펙트 값)을 한 엔진에 다시 보낸다.
        /// MIDI 종류나 기본 엔진을 재생 도중에 손으로 바꿨을 때, 새 엔진이 곡의 설정을 이어받게 하려는 것이다.
        /// 엔진 종류에 맞는 SysEx만 보내고, 보내는 순서는 원래 도착한 순서 그대로다.
        /// </summary>
        public void ReplayStateTo(ISynthEngine target) => ReplayStateTo(target, KindOf(target));

        public void ReplayStateTo(ISynthEngine target, MidiKind kind, int? onlyChannel = null)
        {
            var items = new List<(long seq, Action act)>();
            lock (_replayLock)
            {
                if (onlyChannel is null)
                {
                    foreach (var (seq, d) in _sysexJournal)
                    {
                        bool roland = d.Length > 3 && d[1] == 0x41;
                        bool ok = kind == MidiKind.Mt32 ? roland && d[3] == 0x16
                                                        : !roland || d[3] == 0x42;   // GS/GM 계열: 유니버설/GS만
                        if (kind == MidiKind.Mt32 && !roland) ok = false;
                        if (ok) { var data = d; items.Add((seq, () => target.SysEx(data))); }
                    }
                }
                for (int ch = 0; ch < 16; ch++)
                {
                    if (onlyChannel is int only && only != ch) continue;
                    int c = ch;
                    foreach (int k in ReplayControllers)
                    {
                        if (_ccLast[c, k] < 0) continue;
                        int kk = k, v = _ccLast[c, k];
                        items.Add((_ccSeq[c, k], () => target.ControlChange(c, kk, v)));
                    }
                    if (_programLast[c] >= 0) { int v = _programLast[c]; items.Add((_programSeq[c], () => target.ProgramChange(c, v))); }
                    if (_bendLast[c] >= 0) { int v = _bendLast[c]; items.Add((_bendSeq[c], () => target.PitchBend(c, v))); }
                }
            }
            items.Sort((a, b) => a.seq.CompareTo(b.seq));
            try
            {
                foreach (var it in items) it.act();
            }
            catch { /* 복원에 실패해도 라우팅 변경은 계속 */ }
        }

        private static ChannelState[] CreateDefaultStates()
        {
            var arr = new ChannelState[16];
            for (int i = 0; i < 16; i++)
                arr[i] = new ChannelState { Program = 0, Level = 100, Pan = 64, Reverb = 40, Chorus = 0 };
            return arr;
        }

        /// <summary>채널(0-15)의 마지막으로 관측된 Level/Pan/Reverb/Chorus/Program 값.</summary>
        public ChannelState GetChannelState(int channel) =>
            channel is >= 0 and < 16 ? _channelStates[channel] : _channelStates[0];

        public void RegisterEngine(ISynthEngine engine) => _engines.Add(engine);

        /// <summary>특정 채널(0-15)을 특정 엔진에 고정 배정.</summary>
        public void AssignChannel(int channel, ISynthEngine engine)
        {
            lock (_mapLock)
            {
                var copy = new Dictionary<int, ISynthEngine>(_channelMap) { [channel] = engine };
                _channelMap = copy;
            }
            ReplayStateTo(engine, KindOf(engine), channel);
        }

        private MidiKind KindOf(ISynthEngine engine)
        {
            foreach (var k in new[] { MidiKind.Mt32, MidiKind.GmGs })
                if (ReferenceEquals(KindToEngine?.Invoke(k), engine)) return k;
            return MidiKind.GmGs;
        }

        // ---------------------------------------------------------------
        // MIDI 종류(프리셋) + 자동 감지
        // ---------------------------------------------------------------

        /// <summary>종류 -> 엔진 대응. MainWindow가 채워 준다(Mt32 -> SPE3, GmGs -> SPE5, Sc88 -> 외부 전달).</summary>
        public Func<MidiKind, ISynthEngine?>? KindToEngine { get; set; }

        /// <summary>지금 적용된 종류. Custom이면 사용자가 배정을 직접 관리 중.</summary>
        public MidiKind CurrentKind { get; private set; } = MidiKind.Custom;

        /// <summary>true면 들어오는 SysEx(GM On, GS Reset, SPE3 SysEx)로 종류를 알아채서 자동으로 바꾼다.</summary>
        public bool AutoDetectKind { get; set; }

        /// <summary>종류가 적용될 때마다 발생. 자동 감지면 MIDI 스레드에서 올 수 있으니 UI는 Dispatcher로 넘겨서 쓸 것.</summary>
        public event EventHandler<(MidiKind kind, bool automatic)>? KindChanged;

        /// <summary>
        /// 종류를 적용한다: 개별 채널 배정을 모두 풀고, 기본 엔진을 그 종류의 엔진으로 바꾼다.
        /// 바꾸기 전에 다른 엔진에서 울리던 소리는 끈다(새 엔진이 노트 오프를 받아 줄 수 없어서 걸려 버리기 때문).
        /// </summary>
        public void ApplyKind(MidiKind kind, bool automatic)
        {
            var target = KindToEngine?.Invoke(kind);
            if (kind == MidiKind.Custom || target is null)
            {
                MarkCustom();
                return;
            }

            var previous = new HashSet<ISynthEngine>();
            lock (_mapLock)
            {
                foreach (var e in _channelMap.Values) previous.Add(e);
                if (ActiveEngine is not null) previous.Add(ActiveEngine);
                _channelMap = new Dictionary<int, ISynthEngine>();
                ActiveEngine = target;
                CurrentKind = kind;
            }

            foreach (var e in previous)
            {
                if (ReferenceEquals(e, target)) continue;
                try
                {
                    for (int ch = 0; ch < 16; ch++)
                    {
                        e.ControlChange(ch, 120, 0); // All Sound Off
                        e.ControlChange(ch, 123, 0); // All Notes Off
                    }
                }
                catch { /* 소리 끄기에 실패해도 배정 변경은 계속 */ }
            }

            // 자동 감지는 곡이 시작될 때(초기화 신호) 일어나므로 곡이 설정을 다시 보낸다. 손으로 바꾼 경우만 이어받게 한다.
            if (!automatic && previous.Any(e => !ReferenceEquals(e, target)))
                ReplayStateTo(target, kind);

            KindChanged?.Invoke(this, (kind, automatic));
        }

        /// <summary>사용자가 배정/기본 엔진을 손으로 바꿨다는 표시. 라우팅은 건드리지 않고, 이후 자동 감지가 다시 켜질 수 있게만 한다.</summary>
        public void MarkCustom() => CurrentKind = MidiKind.Custom;

        public IReadOnlyList<ISynthEngine> Engines => _engines;

        private void UpdateStateFromCC(int channel, int controller, int value)
        {
            var s = _channelStates[channel];
            ChannelState next = controller switch
            {
                0 => s with { BankMsb = value },
                7 => s with { Level = value },
                10 => s with { Pan = value },
                91 => s with { Reverb = value },
                93 => s with { Chorus = value },
                _ => s,
            };
            if (controller is 0 or 7 or 10 or 91 or 93)
            {
                _channelStates[channel] = next;
                ChannelStateChanged?.Invoke(this, channel);
            }
        }

        /// <summary>WinMM 등에서 들어온 짧은 MIDI 메시지(3바이트 이하) 처리.</summary>
        public void HandleShortMessage(byte status, byte data1, byte data2)
        {
            var channel = status & 0x0F;
            if (!_channelMap.TryGetValue(channel, out var engine))
                engine = ActiveEngine; // 배정 안 된 채널은 활성 엔진으로

            if (engine is null) return;

            var msg = MidiMessage.Parse(status, data1, data2);
            switch (msg.Type)
            {
                case MidiMessageType.NoteOn:
                    engine.NoteOn(msg.Channel, msg.Data1, msg.Data2);
                    ChannelActivity?.Invoke(this, (msg.Channel, msg.Data2 / 127.0, engine));
                    break;
                case MidiMessageType.NoteOff:
                    engine.NoteOff(msg.Channel, msg.Data1, msg.Data2); break;
                case MidiMessageType.ControlChange:
                    engine.ControlChange(msg.Channel, msg.Data1, msg.Data2);
                    UpdateStateFromCC(msg.Channel, msg.Data1, msg.Data2);
                    Record(msg.Channel, msg.Data1, msg.Data2);
                    break;
                case MidiMessageType.ProgramChange:
                    engine.ProgramChange(msg.Channel, msg.Data1);
                    _channelStates[msg.Channel] = _channelStates[msg.Channel] with { Program = msg.Data1 };
                    lock (_replayLock) { _programLast[msg.Channel] = (short)msg.Data1; _programSeq[msg.Channel] = ++_seq; _stateDirty = true; }
                    ChannelStateChanged?.Invoke(this, msg.Channel);
                    break;
                case MidiMessageType.PitchBend:
                    engine.PitchBend(msg.Channel, msg.Data1);
                    lock (_replayLock) { _bendLast[msg.Channel] = (short)msg.Data1; _bendSeq[msg.Channel] = ++_seq; _stateDirty = true; }
                    break;
            }
        }

        /// <summary>
        /// SysEx 전체 바이트열 처리. 채널 정보가 없는 "장치 전체" 메시지이므로
        /// GlobalPolicy와 장치조회(Device Inquiry, F0 7E .. F7) 여부에 따라
        /// 분배 방식이 달라집니다.
        /// </summary>
        public void HandleSysEx(ReadOnlySpan<byte> data)
        {
            // 종류 자동 감지: 이 SysEx가 다른 종류를 알려 주면 배정을 먼저 바꾸고, 이 메시지도 새 엔진이 받게 한다.
            if (AutoDetectKind)
            {
                var detected = MidiKindDetector.Detect(data);
                if (detected != MidiKind.Custom && detected != CurrentKind)
                    ApplyKind(detected, automatic: true);
            }

            bool isIdentityRequest = data.Length >= 4 && data[0] == 0xF0 && data[1] == 0x7E && data[3] == 0x06;
            if (!isIdentityRequest)
            {
                // 새 곡의 시작 신호(GM On / GS Reset / SPE3 초기화)면 이전 곡의 기록을 버리고 거기서부터 다시 모은다.
                if (MidiKindDetector.IsSongStart(data)) ResetReplayState();
                lock (_replayLock)
                {
                    if (_sysexJournalBytes + data.Length <= SysexJournalMaxBytes)
                    {
                        _sysexJournal.Add((++_seq, data.ToArray())); _stateDirty = true;
                        _sysexJournalBytes += data.Length;
                    }
                }
            }

            bool isDeviceInquiry = data.Length >= 2 && data[0] == 0xF0 && data[1] == 0x7E;

            if (isDeviceInquiry)
            {
                // 장치 조회는 "누가 나인지" 응답하는 메시지라 두 엔진 모두에게
                // 뿌리면 응답이 두 번 오는 등 충돌이 나기 쉽습니다.
                // 활성 엔진이 직접 응답 가능하면 그쪽에만 보냅니다.
                if (ActiveEngine is { HandlesDeviceInquiry: true })
                    ActiveEngine.SysEx(data);
                return;
            }

            if (GlobalPolicy == GlobalSysExPolicy.BroadcastToAll)
            {
                foreach (var e in _engines) e.SysEx(data);
            }
            else
            {
                ActiveEngine?.SysEx(data);
            }
        }
    }
}
