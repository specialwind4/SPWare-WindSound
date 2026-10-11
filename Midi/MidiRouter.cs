using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
        /// <summary>진단용 기록(켜져 있을 때만 MainWindow가 채운다). 들어온 MIDI와 라우터의 판단을 한 줄씩 남긴다.</summary>
        public Action<string>? Trace { get; set; }
        private void T(string s) { var tr = Trace; if (tr != null) tr(s); }
        private static string Hex(ReadOnlySpan<byte> d, int max = 24)
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < d.Length && i < max; i++) { if (i > 0) sb.Append(' '); sb.Append(d[i].ToString("X2")); }
            if (d.Length > max) sb.Append($" ..({d.Length}B)");
            return sb.ToString();
        }

        // 기록은 "재생 구간"(곡 한 곡, 게임 한 판) 단위로 쌓는다. 구간은 곡/게임이 시작할 때 보내는 신호에서 새로 시작한다:
        // GM On / GS Reset / MODE-2 / MT-32 리셋 SysEx, 그리고 16채널 대부분에 한꺼번에 오는 All Notes Off(CC123).
        // (원숭이섬은 시작할 때 SysEx 없이 All Notes Off만 보내고 음색 선택과 음표를 보낸다 - 2026-10-11 실제 기록으로 확인.)
        // 구간마다 계열을 붙인다: MT-32 SysEx가 있었으면 MT-32(1), GS/GM SysEx가 있었으면 GS(0), 없으면 모름(-1).
        // 손으로 종류를 바꿀 때는 "대상과 반대 계열로 밝혀진 구간이 아닌 가장 최근 구간"을 보낸다. 그래서 곡(GS)을 듣다가 게임(모름)으로
        // 돌아와 MT-32를 고르면 게임 구간이, 게임 중에 MT-32를 고르면 지금 게임 구간이 간다.
        private sealed class Journal
        {
            public readonly List<(long seq, byte[] data)> Sysex = new();
            public int SysexBytes;
            public readonly short[,] Cc = NewUnset(16, 128);
            public readonly long[,] CcSeq = new long[16, 128];
            public readonly short[] Program = NewUnset(16);
            public readonly long[] ProgramSeq = new long[16];
            public readonly short[] Bend = NewUnset(16);
            public readonly long[] BendSeq = new long[16];
            public int Family = -1;          // -1 모름, 0 GS/GM, 1 MT-32
            public long StartTick;
            public bool StartedByMarker;     // 곡/게임의 시작 신호로 시작한 구간인가(앱이 켜질 때 만든 첫 구간은 아니다)

            /// <summary>채널 설정(음색 선택, 컨트롤러, 벤드)이 하나라도 있는가. 곡/게임 한 판에는 반드시 있다(떠돌이 SysEx만 있는 구간과 구별).</summary>
            public bool HasChannelState
            {
                get
                {
                    for (int c = 0; c < 16; c++)
                    {
                        if (Program[c] >= 0 || Bend[c] >= 0) return true;
                        for (int k = 0; k < 128; k++) if (Cc[c, k] >= 0) return true;
                    }
                    return false;
                }
            }

            /// <summary>기록된 것이 하나라도 있는가.</summary>
            public bool HasContent
            {
                get
                {
                    if (Sysex.Count > 0) return true;
                    for (int c = 0; c < 16; c++)
                    {
                        if (Program[c] >= 0 || Bend[c] >= 0) return true;
                        for (int k = 0; k < 128; k++) if (Cc[c, k] >= 0) return true;
                    }
                    return false;
                }
            }
        }

        private long _seq;
        private const int SysexJournalMaxBytes = 256 * 1024;
        private const int MaxSegments = 8;
        private readonly List<Journal> _segs = new() { new Journal() };
        private Journal Cur => _segs[_segs.Count - 1];
        private static readonly int[] ReplayControllers = { 0, 32, 7, 10, 11, 71, 72, 73, 74, 91, 93 };

        private static int FamilyOf(MidiKind k) => k == MidiKind.Mt32 ? 1 : 0;

        private static short[] NewUnset(int n) { var a = new short[n]; Array.Fill(a, (short)-1); return a; }
        private static short[,] NewUnset(int a, int b)
        {
            var m = new short[a, b];
            for (int i = 0; i < a; i++) for (int j = 0; j < b; j++) m[i, j] = -1;
            return m;
        }

        /// <summary>새 재생 구간을 시작한다(_replayLock 안에서 부를 것).</summary>
        private Journal NewSegmentLocked(int family, string why)
        {
            // 비어 있는 현재 구간은 그대로 다시 쓴다(신호가 연달아 와도 빈 구간이 쌓이지 않게)
            if (!Cur.HasContent) { Cur.Family = family; Cur.StartTick = Clock(); Cur.StartedByMarker = true; if (Trace != null) T($"SEG reuse empty -> fam={family} ({why})"); return Cur; }
            var j = new Journal { Family = family, StartTick = Clock(), StartedByMarker = true };
            _segs.Add(j);
            while (_segs.Count > MaxSegments) _segs.RemoveAt(0);
            _stateDirty = true;
            if (Trace != null) T($"SEG new fam={family} ({why}) segs={_segs.Count}");
            return j;
        }

        private void Record(int channel, int controller, int value)
        {
            if (Array.IndexOf(ReplayControllers, controller) < 0) return;
            lock (_replayLock) { var j = Cur; j.Cc[channel, controller] = (short)value; j.CcSeq[channel, controller] = ++_seq; _stateDirty = true; }
        }

        private void RecordProgram(int channel, int program)
        {
            lock (_replayLock) { var j = Cur; j.Program[channel] = (short)program; j.ProgramSeq[channel] = ++_seq; _stateDirty = true; }
        }

        private void RecordBend(int channel, int value)
        {
            lock (_replayLock) { var j = Cur; j.Bend[channel] = (short)value; j.BendSeq[channel] = ++_seq; _stateDirty = true; }
        }

        private void RecordShort(int status, int d1, int d2)
        {
            int ch = status & 0x0F;
            switch (status & 0xF0)
            {
                case 0xB0: Record(ch, d1, d2); break;
                case 0xC0: RecordProgram(ch, d1); break;
            }
        }

        // ---- All Notes Off 묶음 = 곡/게임의 시작(또는 정지) ----
        private int _aoMask;
        private long _aoFirst;
        private const int AllOffWindowMs = 150, AllOffChannels = 8, MergeWithStartMs = 1500;

        /// <summary>CC123(All Notes Off)이 짧은 시간에 8개 채널 이상에 오면 새 재생 구간으로 본다.</summary>
        private void TrackAllNotesOff(byte status, byte d1)
        {
            if ((status & 0xF0) != 0xB0 || d1 != 123) return;
            long now = Clock();
            if (now - _aoFirst > AllOffWindowMs) { _aoFirst = now; _aoMask = 0; }
            _aoMask |= 1 << (status & 0x0F);
            if (System.Numerics.BitOperations.PopCount((uint)_aoMask) < AllOffChannels) return;
            _aoMask = 0; _aoFirst = 0;
            lock (_replayLock)
            {
                // 방금 구간을 시작했으면 같은 시작으로 본다(플레이어는 정지/탐색할 때 GS Reset과 All Notes Off를 여러 번 연달아 보낸다)
                if (now - Cur.StartTick < MergeWithStartMs && Cur.StartedByMarker) return;
                NewSegmentLocked(-1, "all notes off");
            }
        }

        /// <summary>SysEx 내용으로 계열을 알아본다. 0 = GS/GM, 1 = MT-32, null = 알 수 없음.</summary>
        private static int? FamilyOfSysEx(ReadOnlySpan<byte> d)
        {
            if (d.Length < 6 || d[0] != 0xF0) return null;
            if (d[1] == 0x7E && d[3] == 0x09 && (d[4] == 0x01 || d[4] == 0x03)) return 0;   // GM On / GM2 On
            if (d[1] == 0x41 && d.Length >= 8) { if (d[3] == 0x16) return 1; if (d[3] == 0x42) return 0; }
            return null;
        }

        private static bool IsMt32Reset(ReadOnlySpan<byte> d) =>
            d.Length >= 8 && d[0] == 0xF0 && d[1] == 0x41 && d[3] == 0x16 && d[4] == 0x12 && ((d[5] << 14) | (d[6] << 7) | d[7]) == (0x7F << 14);

        private long _lastMt32SysexTick;

        /// <summary>밀리초 시계. 시험에서 시뮬레이션 시간으로 바꿀 수 있게 열어 둔다.</summary>
        public Func<long> Clock { get; set; } = () => Environment.TickCount64;

        private void JournalSysEx(ReadOnlySpan<byte> data)
        {
            if (data.Length >= 4 && data[0] == 0xF0 && data[1] == 0x7E && data[3] == 0x06) return;   // 장치 조회는 설정이 아니다
            var fam = FamilyOfSysEx(data);
            if (fam == 1) Interlocked.Exchange(ref _lastMt32SysexTick, Clock());
            bool start = MidiKindDetector.IsSongStart(data) || IsMt32Reset(data) ;
            lock (_replayLock)
            {
                var j = Cur;
                if (start) j = NewSegmentLocked(fam ?? -1, "start sysex");
                else if (fam is int f)
                {
                    if (j.Family == -1) j.Family = f;                                  // 모르던 구간이 이 SysEx로 밝혀졌다
                    else if (j.Family != f) j = NewSegmentLocked(f, "family changed"); // 다른 계열의 소리가 끼어들었다(예: 곡 중에 게임이 MT-32 SysEx)
                }
                if (j.SysexBytes + data.Length <= SysexJournalMaxBytes)
                {
                    j.Sysex.Add((++_seq, data.ToArray())); _stateDirty = true;
                    j.SysexBytes += data.Length;
                }
            }
        }

        // ---- MT-32 게임 뒤에 곡이 중간부터 이어질 때 원래 종류로 돌아오기 ----
        // 주의: 이 규칙이 게임 도중에 잘못 켜지면 게임 소리가 SC-55/88로 넘어가 버린다(실제로 원숭이섬에서 그랬다). 그래서 증거 하나로는 돌아가지 않고,
        // 게임이 꺼 둔 채널의 음표나 게임이 방금 MT-32 초기화를 보낸 직후의 메시지는 증거로 치지 않는다.
        // 게임(MT-32)을 거치고 나면 종류가 MT-32에 머물러 있다. 플레이어가 곡을 중간부터 재생하면 곡 앞의 모드 신호(GM On/GS Reset)가
        // 안 오기 때문에(처음부터 재생하면 신호가 와서 정상이다) 곡이 MT-32 엔진으로 나간다. 실기 MT-32는 파트에 배정되지 않은 채널
        // (기본은 1번 채널과 11~16번)의 음표를 소리 내지 않는다. MT-32가 모르는 메시지(뱅크 셀렉트 CC0/32, 사운드 컨트롤러 CC71~74,
        // 리버브/코러스 CC91/93, 배정 안 된 채널의 프로그램 체인지와 음표)가 짧은 시간에 여러 개 오면 MT-32 음악이 아니라고 본다.
        private static readonly int[] DefaultMt32Assign = { 1, 2, 3, 4, 5, 6, 7, 8, 9 };   // 파트 1~8과 리듬 파트의 MIDI 채널(0부터)
        private readonly int[] _mt32ChanAssign = (int[])DefaultMt32Assign.Clone();
        private MidiKind _lastNonMt32Kind = MidiKind.GmGs;
        private readonly List<(long tick, byte status, byte d1, byte d2, bool weak, short prev, long prevSeq)> _recent = new();   // MT-32 상태에서 최근에 온 CC/프로그램 체인지/음표(기록에 덮어쓴 이전 값 포함)
        private const int WeakWindowMs = 400, WeakThreshold = 3, MT32GraceMs = 2000, RecentMs = 1500;

        /// <summary>MT-32 SysEx(시스템 영역 10 00 0D~15 = 채널 배정, 7F 00 00 = 리셋)를 보고 어느 채널이 MT-32 파트에 배정됐는지 따라간다.</summary>
        private void TrackMt32Assign(ReadOnlySpan<byte> d)
        {
            // F0 41 dev 16 12 a1 a2 a3 data... checksum F7
            if (d.Length < 10 || d[0] != 0xF0 || d[1] != 0x41 || d[3] != 0x16 || d[4] != 0x12) return;
            int addr = (d[5] << 14) | (d[6] << 7) | d[7];
            if (addr == (0x7F << 14)) { Array.Copy(DefaultMt32Assign, _mt32ChanAssign, 9); return; }   // 리셋: 기본 배정으로
            int n = d.Length - 10;
            for (int i = 0; i < n; i++)
            {
                int k = addr + i - ((0x10 << 14) + 0x0D);
                if (k >= 0 && k < 9) _mt32ChanAssign[k] = d[8 + i];
            }
        }

        private bool IsMt32Channel(int channel) => Array.IndexOf(_mt32ChanAssign, channel) >= 0;

        /// <summary>MT-32 상태에서 MT-32 음악이 아니라는 증거가 오면 원래 종류로 돌아간다. 돌아가면 true.</summary>
        private bool CheckRevert(byte status, byte d1, byte d2)
        {
            if (!AutoDetectKind || CurrentKind != MidiKind.Mt32) { if (_recent.Count > 0) _recent.Clear(); return false; }
            int type = status & 0xF0, ch = status & 0x0F;
            long now = Clock();
            bool weak;
            if (type == 0x90)
            {
                // MT-32 파트가 없는 채널의 음표. 하지만 원숭이섬 같은 게임은 안 쓰는 채널의 음량을 0으로 꺼 두고도 그 채널로 음표를 보낸다.
                // 꺼진 채널의 음표는 증거가 아니다. 그리고 음표 하나로는 돌아가지 않는다(아래의 "여러 개 모이면" 규칙을 따른다).
                if (d2 == 0 || IsMt32Channel(ch) || ChannelMuted(ch)) return false;
                weak = true;
            }
            else
            {
                if (type != 0xB0 && type != 0xC0) return false;
                if (type == 0xB0 && d1 is 120 or 121 or 123) return false;   // 모두 끄기/컨트롤러 리셋은 어느 쪽에나 온다
                weak = (type == 0xB0 && d1 is 0 or 32 or 71 or 72 or 73 or 74 or 91 or 93) || (type == 0xC0 && !IsMt32Channel(ch));
            }
            _recent.RemoveAll(w => now - w.tick > RecentMs);
            // 게임이 방금 MT-32 초기화를 보냈다면 아직 게임이다
            if (weak && now - Interlocked.Read(ref _lastMt32SysexTick) >= MT32GraceMs)
            {
                int n = 1; foreach (var w in _recent) if (w.weak && now - w.tick <= WeakWindowMs) n++;
                if (n >= WeakThreshold) { DoRevert(now); return true; }
            }
            // 이 메시지가 지금 구간(게임)의 기록에 덮어쓸 이전 값을 적어 둔다. 돌아가기로 하면 그 기록을 원래대로 되돌려야 한다
            // (안 그러면 곡의 상태 재현이 게임의 음색/음량 기록을 오염시켜서, 나중에 게임으로 돌아올 때 빈 소리가 난다).
            short prev = -1; long prevSeq = 0;
            lock (_replayLock)
            {
                var jj = Cur;
                if (type == 0xC0) { prev = jj.Program[ch]; prevSeq = jj.ProgramSeq[ch]; }
                else if (type == 0xB0 && Array.IndexOf(ReplayControllers, d1) >= 0) { prev = jj.Cc[ch, d1]; prevSeq = jj.CcSeq[ch, d1]; }
            }
            _recent.Add((now, status, d1, d2, weak, prev, prevSeq));
            if (_recent.Count > 128) _recent.RemoveAt(0);
            return false;
        }

        /// <summary>채널의 음량(CC7)이 0으로 꺼져 있는가(지금 구간 기준).</summary>
        private bool ChannelMuted(int ch)
        {
            lock (_replayLock) return Cur.Cc[ch, 7] == 0;
        }

        private void DoRevert(long now)
        {
            if (Trace != null) T($"REVERT from MT-32 (recent={_recent.Count}) -> {_lastNonMt32Kind}");
            var list = new List<(long tick, byte status, byte d1, byte d2, bool weak, short prev, long prevSeq)>();
            foreach (var w in _recent) if (now - w.tick <= RecentMs) list.Add(w);
            _recent.Clear();
            Journal? song = null;
            lock (_replayLock)
            {
                // 이 메시지들이 지금 구간(게임)의 기록에 남긴 변경을 되돌린다(뒤에서부터).
                var j = Cur;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var w = list[i]; int ch = w.status & 0x0F, ty = w.status & 0xF0;
                    if (ty == 0xC0) { j.Program[ch] = w.prev; j.ProgramSeq[ch] = w.prevSeq; }
                    else if (ty == 0xB0 && Array.IndexOf(ReplayControllers, w.d1) >= 0) { j.Cc[ch, w.d1] = w.prev; j.CcSeq[ch, w.d1] = w.prevSeq; }
                }
                // 돌아갈 곡의 기록: 지금 구간(게임)보다 앞의, MT-32로 밝혀지지 않은 가장 최근 구간
                for (int i = _segs.Count - 2; i >= 0; i--) if (_segs[i].Family != 1 && _segs[i].HasContent) { song = _segs[i]; break; }
                // 이후의 메시지는 곡의 새 구간에 쌓는다. 복귀는 "MT-32 음악이 아니다"라는 판단이므로 이 구간은 GS 계열로 표시한다
                // (안 그러면 나중에 손으로 MT-32를 고를 때 게임 구간 대신 이 곡 구간이 골라진다).
                if (j.HasContent) NewSegmentLocked(0, "revert"); else j.Family = 0;
                _stateDirty = true;
            }
            var earlier = new List<(byte status, byte d1, byte d2)>();
            foreach (var w in list) earlier.Add((w.status, w.d1, w.d2));
            RevertFromMt32(earlier, song);
        }

        private void RevertFromMt32(List<(byte status, byte d1, byte d2)> earlier, Journal? song)
        {
            var kind = _lastNonMt32Kind;
            var target = KindToEngine?.Invoke(kind);
            if (target is null) return;
            ApplyKind(kind, automatic: true);
            // 곡을 중간부터 틀면 음색/음량 설정은 MT-32였을 때 이미 지나갔다. 게임 전의 곡 기록을 새 엔진에 다시 넣고,
            // 방금 MT-32로 잘못 간 메시지들(상태 재현 등)도 새 엔진에 보낸다.
            if (song != null) ReplayJournal(target, kind, song, null);
            foreach (var w in earlier)
            {
                try
                {
                    int ch = w.status & 0x0F;
                    int type = w.status & 0xF0;
                    if (type == 0xB0) target.ControlChange(ch, w.d1, w.d2);
                    else if (type == 0xC0) target.ProgramChange(ch, w.d1);
                    else if (type == 0x90) target.NoteOn(ch, w.d1, w.d2);   // 증거가 된 음표도 새 엔진에서 울린다
                }
                catch { }
                RecordShort(w.status, w.d1, w.d2);
            }
        }

        // ---- 앱을 껐다 켜도 곡의 설정을 이어받기 위한 저장/복원 ----
        // 곡/게임이 켜져 있는 채로 앱만 바꾸면(예: 개인 버전 -> 공개 버전) 새 앱은 곡 앞부분에서 한 번 온 설정을 받은 적이 없어 빈 소리가 난다.
        private volatile bool _stateDirty;
        private MidiKind _restoredKind = MidiKind.Custom;
        private Journal? _restoredSeg;
        private bool _restorePending;

        /// <summary>마지막 <see cref="ExportState"/> 뒤로 기록이 바뀌었는가.</summary>
        public bool StateDirty => _stateDirty;

        /// <summary>지금 재생 구간의 기록을 도착 순서대로 바이트열로 만든다. 기록이 없으면 null.</summary>
        public byte[]? ExportState()
        {
            var items = new List<(long seq, byte type, byte[] payload)>();
            MidiKind kind;
            lock (_replayLock)
            {
                _stateDirty = false;
                Journal? j = null;
                for (int i = _segs.Count - 1; i >= 0; i--) if (_segs[i].HasContent) { j = _segs[i]; break; }
                if (j is null) return null;
                // 저장할 종류: 구간의 계열을 따르고, 모르면 지금 종류
                kind = j.Family == 1 ? MidiKind.Mt32 : j.Family == 0 ? (CurrentKind is MidiKind.Mt32 or MidiKind.Custom ? _lastNonMt32Kind : CurrentKind)
                     : MidiKind.Custom;   // 모름: 불러올 때 계열 모름으로, 그때의 기본 엔진에 복원
                foreach (var (seq, d) in j.Sysex) items.Add((seq, 0, d));
                for (int ch = 0; ch < 16; ch++)
                {
                    foreach (int k in ReplayControllers)
                        if (j.Cc[ch, k] >= 0) items.Add((j.CcSeq[ch, k], 1, new[] { (byte)ch, (byte)k, (byte)j.Cc[ch, k] }));
                    if (j.Program[ch] >= 0) items.Add((j.ProgramSeq[ch], 2, new[] { (byte)ch, (byte)j.Program[ch] }));
                    if (j.Bend[ch] >= 0) items.Add((j.BendSeq[ch], 3, new[] { (byte)ch, (byte)(j.Bend[ch] & 0x7F), (byte)(j.Bend[ch] >> 7) }));
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
                var restored = Enum.IsDefined(typeof(MidiKind), kind) ? (MidiKind)kind : MidiKind.Custom;
                lock (_replayLock)
                {
                    int fam = -1;
                    foreach (var (_, d) in sx) if (FamilyOfSysEx(d) is int f) { fam = f; break; }
                    if (fam < 0 && restored != MidiKind.Custom) fam = FamilyOf(restored);
                    var j = new Journal { Family = fam, StartTick = Clock() };
                    foreach (var (s, d) in sx) { j.Sysex.Add((s, d)); j.SysexBytes += d.Length; }
                    foreach (var (ch, k, v, s) in cc) { j.Cc[ch, k] = (short)v; j.CcSeq[ch, k] = s; }
                    foreach (var (ch, v, s) in pr) { j.Program[ch] = (short)v; j.ProgramSeq[ch] = s; }
                    foreach (var (ch, v, s) in pb) { j.Bend[ch] = (short)v; j.BendSeq[ch] = s; }
                    // 불러온 구간은 지난번의 것이다: 지금 구간(비어 있음) 앞에 끼워 둔다. 이후에 오는 메시지는 새 구간에 쌓인다.
                    _segs.Insert(_segs.Count - 1, j);
                    while (_segs.Count > MaxSegments) _segs.RemoveAt(0);
                    _seq = seq; _stateDirty = false;
                    foreach (var (_, d) in sx) TrackMt32Assign(d);   // 저장해 둔 MT-32 채널 배정도 이어받는다(안 그러면 1번 채널을 쓰는 게임이 곡으로 오해받는다)
                    _restoredSeg = j;
                }
                _restoredKind = restored;
                _restorePending = true;
                return true;
            }
            catch { return false; }
        }

        /// <summary>엔진이 열린 직후 호출. 불러온 설정의 종류가 이 엔진의 종류와 같으면 그 설정을 이 엔진에 보낸다(한 번만).</summary>
        public bool RestoreTo(ISynthEngine engine)
        {
            if (!_restorePending || _restoredSeg is null) return false;
            var target = _restoredKind != MidiKind.Custom ? KindToEngine?.Invoke(_restoredKind) : ActiveEngine;
            if (!ReferenceEquals(target, engine)) return false;
            _restorePending = false;
            ReplayJournal(engine, KindOf(engine), _restoredSeg, null);
            return true;
        }

        /// <summary>
        /// 곡/게임이 이미 보낸 설정(SysEx 기록 + 채널별 음색/음량/팬/이펙트 값)을 한 엔진에 다시 보낸다.
        /// MIDI 종류나 기본 엔진을 재생 도중에 손으로 바꿨을 때, 새 엔진이 그 설정을 이어받게 하려는 것이다.
        /// 보낼 구간: 대상 엔진의 반대 계열로 밝혀진 구간이 아닌 가장 최근 구간(없으면 지금 구간). SysEx는 대상 엔진 종류에 맞는 것만 거르고,
        /// 순서는 원래 도착한 순서 그대로다.
        /// </summary>
        public void ReplayStateTo(ISynthEngine target) => ReplayStateTo(target, KindOf(target));

        // ---- 지금 눌려 있는 음 ----
        // 손으로 엔진을 바꾸면 이전 엔진의 소리는 끄는데(노트 오프를 새 엔진이 받아 줄 수 없어서), 길게 끄는 음(원숭이섬 타이틀처럼)은
        // 다음에 다시 칠 때까지 새 엔진에서 빠진다. 실제 녹음으로 확인: 바꾼 뒤 3~4초 동안 전체 -2.5 dB, 고음 -7 dB.
        // 그래서 눌려 있는 음을 기억했다가 새 엔진에서 다시 울린다.
        private readonly byte[,] _held = new byte[16, 128];

        private void TrackHeld(int status, int d1, int d2)
        {
            int type = status & 0xF0, ch = status & 0x0F;
            if (type == 0x90) _held[ch, d1] = (byte)d2;            // 벨로시티 0은 노트 오프
            else if (type == 0x80) _held[ch, d1] = 0;
            else if (type == 0xB0 && d1 is 120 or 123) for (int n = 0; n < 128; n++) _held[ch, n] = 0;
        }

        private void ResendHeld(ISynthEngine target, int? onlyChannel)
        {
            int count = 0;
            try
            {
                for (int ch = 0; ch < 16; ch++)
                {
                    if (onlyChannel is int only && only != ch) continue;
                    for (int n = 0; n < 128; n++) { byte v = _held[ch, n]; if (v > 0) { target.NoteOn(ch, n, v); count++; } }
                }
            }
            catch { }
            if (Trace != null && count > 0) T($"HELD resent {count} notes -> {target.Name}");
        }

        public void ReplayStateTo(ISynthEngine target, MidiKind kind, int? onlyChannel = null)
        {
            ReplayStateToCore(target, kind, onlyChannel);
            ResendHeld(target, onlyChannel);
        }

        private void ReplayStateToCore(ISynthEngine target, MidiKind kind, int? onlyChannel)
        {
            Journal j;
            lock (_replayLock)
            {
                int fam = FamilyOf(kind), opposite = 1 - fam;
                j = Cur;
                if (!Cur.HasChannelState && Cur.Family == fam && Cur.HasContent)
                {
                    // 같은 계열의 곡이 방금 시작했다(GS Reset 등만 받았다): 그 곡이 곧 자기 설정을 보내므로 이전 곡의 설정은 보내지 않는다
                }
                else if (!Cur.HasChannelState || Cur.Family == opposite)
                {
                    // 지금 구간이 비었거나(정지 직후) 반대 계열이다(곡 -> 게임으로 돌아옴): 대상과 반대로 밝혀지지 않은, 채널 설정이 있는 가장 최근 구간
                    Journal? found = null;
                    for (int i = _segs.Count - 1; i >= 0; i--)
                        if (_segs[i].Family != opposite && _segs[i].HasChannelState) { found = _segs[i]; break; }
                    if (found != null) j = found;
                    else if (!Cur.HasContent)
                    {
                        if (Trace != null) T($"REPLAY skipped -> {target.Name}: nothing to carry over");
                        return;
                    }
                }
            }
            ReplayJournal(target, kind, j, onlyChannel);
        }

        private void ReplayJournal(ISynthEngine target, MidiKind kind, Journal j, int? onlyChannel)
        {
            var items = new List<(long seq, Action act)>();
            int segIndex;
            lock (_replayLock)
            {
                segIndex = _segs.IndexOf(j);
                if (onlyChannel is null)
                {
                    foreach (var (seq, d) in j.Sysex)
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
                        if (j.Cc[c, k] < 0) continue;
                        int kk = k, v = j.Cc[c, k];
                        items.Add((j.CcSeq[c, k], () => target.ControlChange(c, kk, v)));
                    }
                    if (j.Program[c] >= 0) { int v = j.Program[c]; items.Add((j.ProgramSeq[c], () => target.ProgramChange(c, v))); }
                    if (j.Bend[c] >= 0) { int v = j.Bend[c]; items.Add((j.BendSeq[c], () => target.PitchBend(c, v))); }
                }
            }
            items.Sort((a, b) => a.seq.CompareTo(b.seq));
            if (Trace != null) T($"REPLAY -> {target.Name} kind={kind} seg={segIndex}/{_segs.Count} fam={j.Family} items={items.Count}" + (onlyChannel is int oc ? $" ch={oc}" : ""));
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
                if (kind != MidiKind.Mt32 && kind != MidiKind.Custom) _lastNonMt32Kind = kind;
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

            if (Trace != null) T($"KIND -> {kind} ({(automatic ? "auto" : "manual")}) segs={_segs.Count} curfam={Cur.Family}");
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
            TrackAllNotesOff(status, data1);   // 모든 채널 All Notes Off = 곡/게임의 시작 -> 새 재생 구간
            TrackHeld(status, data1, data2);
            // 자동 감지가 켜져 있고 MT-32인데 MT-32 음악이 아니라는 증거가 오면 원래 종류로 돌아간다
            CheckRevert(status, data1, data2);

            var channel = status & 0x0F;
            if (!_channelMap.TryGetValue(channel, out var engine))
                engine = ActiveEngine; // 배정 안 된 채널은 활성 엔진으로

            if (engine is null) return;

            var msg = MidiMessage.Parse(status, data1, data2);
            if (Trace != null) T($"IN  {status:X2} {data1:X2} {data2:X2}  kind={CurrentKind} -> {engine.Name}");
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
                    RecordProgram(msg.Channel, msg.Data1);
                    ChannelStateChanged?.Invoke(this, msg.Channel);
                    break;
                case MidiMessageType.PitchBend:
                    engine.PitchBend(msg.Channel, msg.Data1);
                    RecordBend(msg.Channel, msg.Data1);
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
            if (Trace != null) T($"SX  {Hex(data)}  kind={CurrentKind}");
            // 종류 자동 감지: 이 SysEx가 다른 종류를 알려 주면 배정을 먼저 바꾸고, 이 메시지도 새 엔진이 받게 한다.
            if (AutoDetectKind)
            {
                var detected = MidiKindDetector.Detect(data);
                if (detected != MidiKind.Custom && detected != CurrentKind)
                    ApplyKind(detected, automatic: true);
            }

            TrackMt32Assign(data);
            JournalSysEx(data);

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
