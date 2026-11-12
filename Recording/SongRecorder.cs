using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using NAudio.MediaFoundation;
using NAudio.Wave;

namespace SPWare.VirtualSoundCanvas.Recording
{
    /// <summary>
    /// 이 앱으로 듣고 있는 음악을 파일로 남긴다: 받은 MIDI는 표준 MIDI 파일(.mid)로, 실제로 낸 소리는 MP3로.
    ///
    /// 두 가지 방식:
    ///  - 수동: 시작을 누른 때부터 정지를 누를 때까지를 파일 하나로.
    ///  - 곡 단위: 켜 두면 곡이 시작될 때(첫 음) 녹음을 시작하고, 곡이 끝나거나(조용해짐) 다음 곡이 시작되거나(초기화 신호)
    ///    같은 음들이 되풀이되기 시작하면(반복 음악: 한 사이클만) 파일 하나로 저장한다. 이미 저장한 곡과 같은 곡은 다시 저장하지 않는다.
    ///
    /// 스레드: MIDI 스레드가 <see cref="OnShort"/>/<see cref="OnSysEx"/>/<see cref="OnMarker"/>를, 오디오 스레드가 <see cref="OnAudio"/>를,
    /// UI 스레드가 <see cref="Start"/>/<see cref="Stop"/>/<see cref="Tick"/>을 부른다. 파일 쓰기와 MP3 변환은 전용 작업 스레드가 한다
    /// (오디오 스레드는 큐에 넣기만 한다: 디스크를 기다리다 소리가 끊기지 않게).
    /// </summary>
    public sealed class SongRecorder : IDisposable
    {
        public enum Phase { Off, Armed, Recording, Holding }

        public sealed class Options
        {
            public bool SaveMidi = true;
            public bool SaveMp3 = true;
            /// <summary>true = 곡 단위 자동 저장, false = 수동(시작~정지).</summary>
            public bool PerSong = true;
            public string Folder = "";
            public int SampleRate = 44100;
            public string TrackName = "SPWare Wind Sound";
            public string FilePrefix = "WindSound";
            /// <summary>반복으로 인정할 최소 주기(밀리초). 이보다 짧은 되풀이는 곡 안의 반복 악절로 본다.</summary>
            public int LoopMinPeriodMs = 15000;
        }

        private enum EndReason { Stop, Silence, Marker, Loop, TooLong }

        private sealed class Take
        {
            public readonly Stopwatch Clock = Stopwatch.StartNew();
            public readonly DateTime StartedAt = DateTime.Now;
            public readonly List<(long ms, byte[] data)> Events = new();
            public List<byte[]> Preamble = new();
            public readonly LoopDetector Loop = new();
            public readonly byte[,] Held = new byte[16, 128];
            public int HeldCount;
            public bool HeldAtStart;
            public int NoteOns;
            public long FirstNoteMs = -1;
            public long LastNoteMs;
            public long LastLoudMs;                       // 오디오 스레드가 쓴다
            public readonly ConcurrentQueue<short[]> Audio = new();
            public string PcmPath = "";
            public FileStream? Pcm;                       // 작업 스레드만 만진다
            public bool WantAudio;
        }

        private sealed class EndRequest
        {
            public Take Take = null!;
            public long EndMs;
            public EndReason Reason;
            public Options Opt = null!;
            public ulong Print;   // 이 곡의 지문(같은 곡을 또 저장하지 않으려고). 녹음을 끝낼 때 계산해 둔다
        }

        private readonly object _lock = new();
        private Options _opt = new();
        private Phase _phase = Phase.Off;
        private Take? _take;            // 지금 녹음 중인 것(오디오 스레드도 읽는다)
        private LoopDetector? _follow;  // 반복을 찾아 저장한 뒤, 같은 곡이 계속 도는지 따라가는 것
        private long _followLastNoteTick;
        private readonly HashSet<ulong> _savedSongs = new();

        private readonly ConcurrentQueue<EndRequest> _ends = new();
        private readonly List<Take> _open = new();        // 작업 스레드가 오디오를 받아 적고 있는 것들
        private readonly AutoResetEvent _wake = new(false);
        private readonly Thread _worker;
        private volatile bool _quit;
        private int _pending;                             // 저장 중인 개수

        /// <summary>지금 상태(설정)를 MIDI 메시지 목록으로 돌려준다. 인수: 눌려 있는 음도 넣을지.</summary>
        public Func<bool, List<byte[]>>? StateSnapshot { get; set; }

        /// <summary>알림 한 줄(저장했다, 건너뛰었다 등). 아무 스레드에서나 온다.</summary>
        public event Action<string>? Message;

        public Phase CurrentPhase { get { lock (_lock) return _phase; } }
        public bool IsOn => CurrentPhase != Phase.Off;
        public bool IsSaving => Volatile.Read(ref _pending) > 0;
        public int SavedCount { get; private set; }
        public string LastSavedName { get; private set; } = "";

        /// <summary>지금 녹음 중인 곡이 시작된 지 얼마나 됐는가(녹음 중이 아니면 0).</summary>
        public TimeSpan TakeElapsed
        {
            get { var t = Volatile.Read(ref _take); return t is null ? TimeSpan.Zero : t.Clock.Elapsed; }
        }

        public SongRecorder()
        {
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "SongRecorder" };
            _worker.Start();
            // 예전에 비정상 종료로 남은 임시 녹음 파일을 치운다(이틀 넘은 것만: 다른 창이 지금 쓰는 파일은 건드리지 않는다)
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "SPWare.WindSound");
                if (Directory.Exists(dir))
                    foreach (var f in Directory.GetFiles(dir, "rec_*.pcm"))
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromDays(2)) TryDelete(f);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // 시작 / 정지 (UI 스레드)
        // ------------------------------------------------------------------

        public void Start(Options options)
        {
            lock (_lock)
            {
                if (_phase != Phase.Off) return;
                _opt = options;
                lock (_savedSongs) _savedSongs.Clear();
                _follow = null;
                if (options.PerSong) _phase = Phase.Armed;
                else BeginTakeLocked(includeHeld: true);
            }
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (_phase == Phase.Off) return;
                if (_take != null) EndTakeLocked(_take.Clock.ElapsedMilliseconds, EndReason.Stop);
                _follow = null;
                _phase = Phase.Off;
            }
        }

        private void BeginTakeLocked(bool includeHeld)
        {
            var t = new Take { WantAudio = _opt.SaveMp3 };
            t.Loop.MinPeriodMs = _opt.LoopMinPeriodMs;
            try { t.Preamble = StateSnapshot?.Invoke(includeHeld) ?? new List<byte[]>(); } catch { }
            foreach (var m in t.Preamble)
                if (m.Length == 3 && (m[0] & 0xF0) == 0x90 && m[2] > 0) { t.Held[m[0] & 0x0F, m[1]] = m[2]; t.HeldCount++; t.HeldAtStart = true; }
            if (t.WantAudio)
            {
                string dir = Path.Combine(Path.GetTempPath(), "SPWare.WindSound");
                try { Directory.CreateDirectory(dir); } catch { }
                t.PcmPath = Path.Combine(dir, $"rec_{Guid.NewGuid():N}.pcm");
            }
            lock (_open) _open.Add(t);
            Volatile.Write(ref _take, t);
            _phase = Phase.Recording;
        }

        private void EndTakeLocked(long endMs, EndReason reason)
        {
            var t = _take;
            if (t is null) return;
            Volatile.Write(ref _take, null);
            Interlocked.Increment(ref _pending);
            _ends.Enqueue(new EndRequest { Take = t, EndMs = endMs, Reason = reason, Opt = _opt, Print = _opt.PerSong ? t.Loop.Fingerprint(endMs) : 0 });
            _wake.Set();
        }

        // ------------------------------------------------------------------
        // MIDI (MIDI 스레드)
        // ------------------------------------------------------------------

        public void OnShort(byte status, byte d1, byte d2)
        {
            if (_phase == Phase.Off) return;   // 꺼져 있을 때는 락도 잡지 않는다
            lock (_lock)
            {
                if (_phase == Phase.Off) return;
                int type = status & 0xF0, ch = status & 0x0F;
                bool noteOn = type == 0x90 && d2 > 0;
                bool noteOff = type == 0x80 || (type == 0x90 && d2 == 0);

                if (_phase == Phase.Holding)
                {
                    if (!noteOn) return;
                    long now = Environment.TickCount64;
                    if (_follow != null && _follow.Follow(now, LoopDetector.KeyOf(ch, d1, d2))) { _followLastNoteTick = now; return; }
                    // 반복에 없는 음이 왔다: 다른 곡이 시작됐다 -> 이 음부터 새로 녹음한다
                    _follow = null;
                    _phase = Phase.Armed;
                }
                if (_phase == Phase.Armed)
                {
                    if (!noteOn) return;       // 곡 앞의 설정(음색, 음량)은 라우터 기록에 쌓이고, 녹음을 시작할 때 맨 앞에 넣는다
                    BeginTakeLocked(includeHeld: false);
                }

                var t = _take;
                if (t is null) return;
                long ms = t.Clock.ElapsedMilliseconds;
                int len = type is 0xC0 or 0xD0 ? 2 : 3;
                t.Events.Add((ms, len == 2 ? new[] { status, d1 } : new[] { status, d1, d2 }));

                if (noteOn)
                {
                    if (t.Held[ch, d1] == 0) t.HeldCount++;
                    t.Held[ch, d1] = d2;
                    t.NoteOns++;
                    if (t.FirstNoteMs < 0) t.FirstNoteMs = ms;
                    t.LastNoteMs = ms;
                    if (_opt.PerSong)
                    {
                        var loop = t.Loop.Add(ms, LoopDetector.KeyOf(ch, d1, d2));
                        if (loop is LoopDetector.Loop found)
                        {
                            EndTakeLocked(found.CycleEndMs, EndReason.Loop);
                            _follow = t.Loop;
                            _followLastNoteTick = Environment.TickCount64;
                            _phase = Phase.Holding;
                        }
                    }
                }
                else if (noteOff)
                {
                    if (t.Held[ch, d1] != 0) { t.Held[ch, d1] = 0; t.HeldCount--; }
                    t.LastNoteMs = ms;
                }
                else if (type == 0xB0 && d1 is 120 or 123)
                {
                    for (int n = 0; n < 128; n++) if (t.Held[ch, n] != 0) { t.Held[ch, n] = 0; t.HeldCount--; }
                }
            }
        }

        public void OnSysEx(ReadOnlySpan<byte> data)
        {
            if (_phase != Phase.Recording || data.Length < 2 || data[0] != 0xF0) return;
            var copy = data.ToArray();
            lock (_lock)
            {
                var t = _take;
                if (_phase == Phase.Recording && t != null) t.Events.Add((t.Clock.ElapsedMilliseconds, copy));
            }
        }

        /// <summary>라우터가 "새 곡/게임이 시작됐다"는 신호(GM On, GS Reset, MT-32 리셋, 모든 채널 All Notes Off)를 봤을 때.</summary>
        public void OnMarker()
        {
            if (_phase == Phase.Off) return;
            lock (_lock)
            {
                if (!_opt.PerSong) return;
                if (_phase == Phase.Recording && _take != null)
                {
                    EndTakeLocked(_take.Clock.ElapsedMilliseconds, EndReason.Marker);
                    _phase = Phase.Armed;
                }
                else if (_phase == Phase.Holding)
                {
                    _follow = null;
                    _phase = Phase.Armed;
                }
            }
        }

        // ------------------------------------------------------------------
        // 주기 점검 (UI 타이머): 곡이 끝났는지
        // ------------------------------------------------------------------

        private const int SilenceAfterNotesMs = 2000, SilenceAudioMs = 700, SilenceHardCapMs = 15000;
        private const int HoldReleaseMs = 3000;
        private const long MaxTakeMs = 60L * 60 * 1000, MaxManualMs = 180L * 60 * 1000;

        public void Tick()
        {
            if (_phase == Phase.Off) return;
            lock (_lock)
            {
                var t = _take;
                if (_phase == Phase.Recording && t != null)
                {
                    long now = t.Clock.ElapsedMilliseconds;
                    if (_opt.PerSong)
                    {
                        long sinceNote = now - t.LastNoteMs;
                        bool quiet = !t.WantAudio || now - Volatile.Read(ref t.LastLoudMs) >= SilenceAudioMs;
                        if ((t.HeldCount == 0 && sinceNote >= SilenceAfterNotesMs && quiet) || sinceNote >= SilenceHardCapMs)
                        {
                            // 끝은 마지막 소리가 난 때에서 조금 뒤(확인하느라 기다린 조용한 시간까지 파일에 넣지 않는다)
                            long lastSound = Math.Max(t.LastNoteMs, t.WantAudio ? Volatile.Read(ref t.LastLoudMs) : 0);
                            EndTakeLocked(Math.Min(now, lastSound + 600), EndReason.Silence);
                            _phase = Phase.Armed;
                        }
                        else if (now >= MaxTakeMs)
                        {
                            EndTakeLocked(now, EndReason.TooLong);
                            _phase = Phase.Armed;
                        }
                    }
                    else if (now >= MaxManualMs)
                    {
                        EndTakeLocked(now, EndReason.TooLong);
                        _phase = Phase.Off;
                    }
                }
                else if (_phase == Phase.Holding && Environment.TickCount64 - _followLastNoteTick >= HoldReleaseMs)
                {
                    _follow = null;      // 반복하던 곡이 멎었다: 다음 곡을 기다린다
                    _phase = Phase.Armed;
                }
            }
        }

        // ------------------------------------------------------------------
        // 오디오 (오디오 스레드): 큐에 넣기만 한다
        // ------------------------------------------------------------------

        public void OnAudio(float[] buffer, int offset, int count)
        {
            var t = Volatile.Read(ref _take);
            if (t is null) return;
            float peak = 0f;
            if (t.WantAudio)
            {
                var block = new short[count];
                for (int i = 0; i < count; i++)
                {
                    float v = buffer[offset + i];
                    float a = v < 0 ? -v : v;
                    if (a > peak) peak = a;
                    int s = (int)MathF.Round(v * 32767f);
                    block[i] = (short)(s > 32767 ? 32767 : s < -32768 ? -32768 : s);
                }
                t.Audio.Enqueue(block);
                _wake.Set();
            }
            else
            {
                for (int i = 0; i < count; i++) { float a = buffer[offset + i]; if (a < 0) a = -a; if (a > peak) peak = a; }
            }
            if (peak > 0.001f) Volatile.Write(ref t.LastLoudMs, t.Clock.ElapsedMilliseconds);   // 약 -60 dB보다 크면 "소리가 나고 있다"
        }

        // ------------------------------------------------------------------
        // 작업 스레드: 오디오를 임시 파일에 적고, 끝난 녹음을 파일로 만든다
        // ------------------------------------------------------------------

        private void WorkerLoop()
        {
            while (!_quit)
            {
                _wake.WaitOne(250);
                try
                {
                    Take[] open;
                    lock (_open) open = _open.ToArray();
                    foreach (var t in open) Drain(t);

                    while (_ends.TryDequeue(out var req))
                    {
                        try
                        {
                            Drain(req.Take);
                            req.Take.Pcm?.Dispose();
                            req.Take.Pcm = null;
                            lock (_open) _open.Remove(req.Take);
                            Finish(req);
                        }
                        catch (Exception ex) { Say($"녹음 저장 실패: {ex.Message}"); }
                        finally
                        {
                            TryDelete(req.Take.PcmPath);
                            Interlocked.Decrement(ref _pending);
                        }
                    }
                }
                catch { /* 다음 바퀴에서 다시 */ }
            }
        }

        private static void Drain(Take t)
        {
            if (!t.WantAudio) return;
            while (t.Audio.TryDequeue(out var block))
            {
                t.Pcm ??= new FileStream(t.PcmPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read, 1 << 16);
                var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(block.AsSpan());
                t.Pcm.Write(bytes);
            }
        }

        private const int MinSongNotes = 8, MinSongMs = 2500;
        private const int LoopTailMs = 3000;          // 반복 음악: 한 사이클 뒤 이만큼 더 넣고 서서히 줄인다
        private const int SysExSpacingMs = 20;

        private void Finish(EndRequest req)
        {
            var t = req.Take;
            var opt = req.Opt;
            long endMs = Math.Max(req.EndMs, 0);

            if (t.NoteOns == 0 && !t.HeldAtStart)
            {
                if (!opt.PerSong) Say("녹음된 음이 없어서 저장하지 않았습니다");
                return;
            }
            long startCut = t.HeldAtStart || t.FirstNoteMs < 0 ? 0 : Math.Max(0, t.FirstNoteMs - 80);
            if (opt.PerSong)
            {
                if (t.NoteOns < MinSongNotes || endMs - startCut < MinSongMs) return;   // 효과음이나 잠깐 울린 소리는 곡으로 치지 않는다
                bool isNew;
                lock (_savedSongs) isNew = _savedSongs.Add(req.Print);
                if (!isNew) { Say("이미 저장한 곡과 같아서 건너뛰었습니다"); return; }
            }

            Directory.CreateDirectory(opt.Folder);
            string baseName = $"{opt.FilePrefix} {t.StartedAt:yyyy-MM-dd HH.mm.ss}";
            string basePath = Path.Combine(opt.Folder, baseName);
            for (int n = 2; File.Exists(basePath + ".mid") || File.Exists(basePath + ".mp3") || File.Exists(basePath + ".wav"); n++)
                basePath = Path.Combine(opt.Folder, $"{baseName} ({n})");

            var saved = new List<string>();
            if (opt.SaveMidi)
            {
                WriteMidi(t, basePath + ".mid", startCut, endMs, req.Reason, opt.TrackName);
                saved.Add(Path.GetFileName(basePath + ".mid"));
            }
            if (opt.SaveMp3 && File.Exists(t.PcmPath))
            {
                string? audio = WriteAudio(t, basePath, startCut, endMs, req.Reason, opt.SampleRate);
                if (audio != null) saved.Add(Path.GetFileName(audio));
            }
            if (saved.Count == 0) return;

            SavedCount++;
            LastSavedName = saved[0];
            string why = req.Reason switch
            {
                EndReason.Loop => " (반복 음악: 한 사이클)",
                EndReason.Marker => " (다음 곡이 시작됨)",
                EndReason.Silence => " (곡이 끝남)",
                EndReason.TooLong => " (최대 길이)",
                _ => "",
            };
            long length = endMs - startCut;
            Say($"녹음 저장: {string.Join(", ", saved)} - {length / 60000}분 {length / 1000 % 60}초{why}");
        }

        private static void WriteMidi(Take t, string path, long startCut, long endMs, EndReason reason, string trackName)
        {
            var list = new List<(long ms, byte[] data)>();
            long lead = 0;
            void Setup(byte[] m)
            {
                list.Add((lead, m));
                if (m.Length > 0 && m[0] == 0xF0) lead += SysExSpacingMs;   // SysEx 사이를 조금 띄운다(실기나 다른 재생기가 놓치지 않게)
            }
            // 1) 녹음을 시작할 때의 상태(곡이 이미 보내 둔 음색/음량/SysEx)
            foreach (var m in t.Preamble) Setup(m);

            // 2) 받은 메시지. 첫 음보다 앞의 것은 설정이므로 맨 앞에 붙이고, 첫 음부터는 원래 간격대로.
            long lastNote = t.LastNoteMs;
            var held = new byte[16, 128];
            foreach (var m in t.Preamble)
                if (m.Length == 3 && (m[0] & 0xF0) == 0x90 && m[2] > 0) held[m[0] & 0x0F, m[1]] = 1;
            long last = lead;
            foreach (var (ms, d) in t.Events)
            {
                if (ms >= endMs) break;
                if (reason == EndReason.Marker && ms > lastNote) break;   // 마지막 음 뒤의 메시지는 다음 곡으로 넘어가는 신호다
                if (ms < startCut) { Setup(d); last = lead; continue; }
                long at = lead + (ms - startCut);
                list.Add((at, d));
                last = at;
                if (d.Length == 3)
                {
                    int type = d[0] & 0xF0, ch = d[0] & 0x0F;
                    if (type == 0x90) held[ch, d[1]] = (byte)(d[2] > 0 ? 1 : 0);
                    else if (type == 0x80) held[ch, d[1]] = 0;
                    else if (type == 0xB0 && d[1] is 120 or 123) for (int n = 0; n < 128; n++) held[ch, n] = 0;
                }
            }
            // 3) 끝: 아직 눌려 있는 음을 끈다(반복 지점에서 자르면 걸쳐 있는 음이 있다)
            long end = Math.Max(lead + Math.Max(endMs - startCut, 0), last);
            if (reason == EndReason.Marker) end = last;
            for (int ch = 0; ch < 16; ch++)
                for (int n = 0; n < 128; n++)
                    if (held[ch, n] != 0) list.Add((end, new[] { (byte)(0x80 | ch), (byte)n, (byte)0x40 }));
            SmfWriter.Write(path, list, end, trackName);
        }

        private static bool _mfStarted;

        /// <summary>임시 파일의 해당 구간을 MP3로 만든다(크기는 -1 dB에 맞춘다). MP3 변환기를 못 쓰면 WAV로 저장한다. 저장한 파일 경로를 돌려준다.</summary>
        private string? WriteAudio(Take t, string basePath, long startCut, long endMs, EndReason reason, int sampleRate)
        {
            using var fs = new FileStream(t.PcmPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
            long totalFrames = fs.Length / 4;
            long startFrame = Math.Min(startCut * sampleRate / 1000, totalFrames);
            long tail = reason == EndReason.Loop ? LoopTailMs : 0;
            long endFrame = Math.Min((endMs + tail) * sampleRate / 1000, totalFrames);
            if (endFrame - startFrame < sampleRate / 2) return null;   // 0.5초도 안 되면 소리 파일은 만들지 않는다
            long fadeFrames = reason switch
            {
                EndReason.Loop => Math.Min(tail * sampleRate / 1000, endFrame - startFrame),
                EndReason.Marker or EndReason.Stop or EndReason.TooLong => Math.Min(sampleRate * 40 / 1000, endFrame - startFrame),   // 뚝 끊기는 소리(틱)를 막는 짧은 줄임
                _ => 0,
            };

            // 가장 큰 소리를 찾아 -1 dB가 되게 키운다(믹서 출력은 여유를 많이 둬서 그대로 저장하면 작다). 너무 작은 소리를 과하게 키우지는 않는다.
            int peak = 0;
            var buf = new byte[1 << 16];
            fs.Position = startFrame * 4;
            long left = (endFrame - startFrame) * 4;
            while (left > 0)
            {
                int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                if (n <= 0) break;
                left -= n;
                for (int i = 0; i + 1 < n; i += 2)
                {
                    int s = (short)(buf[i] | (buf[i + 1] << 8));
                    if (s < 0) s = -s;
                    if (s > peak) peak = s;
                }
            }
            if (peak < 8) return null;   // 사실상 무음
            float gain = Math.Min(0.891f * 32767f / peak, 15.85f);   // 최대 +24 dB

            var format = new WaveFormat(sampleRate, 16, 2);
            try
            {
                if (!_mfStarted) { MediaFoundationApi.Startup(); _mfStarted = true; }
                string mp3 = basePath + ".mp3";
                MediaFoundationEncoder.EncodeToMp3(new PcmSegment(fs, format, startFrame, endFrame, fadeFrames, gain), mp3, 192000);
                if (new FileInfo(mp3).Length > 1024) return mp3;
                TryDelete(mp3);
            }
            catch (Exception ex)
            {
                TryDelete(basePath + ".mp3");
                Say($"MP3 변환을 쓸 수 없어 WAV로 저장합니다 ({ex.Message})");
            }
            string wav = basePath + ".wav";
            WaveFileWriter.CreateWaveFile(wav, new PcmSegment(fs, format, startFrame, endFrame, fadeFrames, gain));
            return wav;
        }

        /// <summary>임시 PCM 파일의 한 구간을 크기 조절과 끝부분 줄임을 넣어 읽어 주는 것.</summary>
        private sealed class PcmSegment : IWaveProvider
        {
            private readonly FileStream _fs;
            private readonly long _end, _fadeStart, _fadeFrames;
            private readonly float _gain;
            private long _pos;
            public WaveFormat WaveFormat { get; }

            public PcmSegment(FileStream fs, WaveFormat format, long startFrame, long endFrame, long fadeFrames, float gain)
            {
                _fs = fs; WaveFormat = format; _pos = startFrame; _end = endFrame;
                _fadeFrames = fadeFrames; _fadeStart = endFrame - fadeFrames; _gain = gain;
            }

            public int Read(byte[] buffer, int offset, int count)
            {
                long framesLeft = _end - _pos;
                int frames = (int)Math.Min(count / 4, framesLeft);
                if (frames <= 0) return 0;
                _fs.Position = _pos * 4;
                int got = 0, want = frames * 4;
                while (got < want)
                {
                    int n = _fs.Read(buffer, offset + got, want - got);
                    if (n <= 0) break;
                    got += n;
                }
                frames = got / 4;
                for (int f = 0; f < frames; f++)
                {
                    float g = _gain;
                    long frame = _pos + f;
                    if (_fadeFrames > 0 && frame >= _fadeStart) g *= (float)(_end - frame) / _fadeFrames;
                    for (int c = 0; c < 2; c++)
                    {
                        int i = offset + (f * 2 + c) * 2;
                        int s = (short)(buffer[i] | (buffer[i + 1] << 8));
                        int v = (int)MathF.Round(s * g);
                        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                        buffer[i] = (byte)v; buffer[i + 1] = (byte)(v >> 8);
                    }
                }
                _pos += frames;
                return frames * 4;
            }
        }

        private void Say(string text) { try { Message?.Invoke(text); } catch { } }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>앱을 닫을 때: 녹음 중이면 멈추고, 저장이 끝날 때까지 잠깐 기다린다.</summary>
        public void Shutdown(int waitMs = 20000)
        {
            Stop();
            var sw = Stopwatch.StartNew();
            while (IsSaving && sw.ElapsedMilliseconds < waitMs) { _wake.Set(); Thread.Sleep(50); }
            _quit = true;
            _wake.Set();
        }

        public void Dispose() => Shutdown(0);
    }
}
