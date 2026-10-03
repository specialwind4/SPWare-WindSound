using System;
using System.Runtime.InteropServices;

namespace SPWare.VirtualSoundCanvas.Engines
{
    /// <summary>
    /// SPE5 엔진. linoshkmalayil/Nuked-SC55-GUI-Float(jcmoyer/Nuked-SC55의
    /// 포크, https://github.com/linoshkmalayil/Nuked-SC55-GUI-Float) 소스를 직접 열어서
    /// 확인한 실제 공개 API(Emulator 클래스, emu.h)를 그대로 감싼 얇은 C 래퍼를 씁니다.
    ///
    /// 이전 버전과 다른 점: 전에는 함수 이름을 추측해서 짜뒀었는데, 이제는 실제
    /// 소스(src/backend/emu.h, rom_io.h, pcm.h)를 열어 Init/LoadRoms/PostMIDI/Step/
    /// SetSampleCallback/GetLCD 등 진짜 시그니처를 확인하고, 그걸 감싸는
    /// sc55_wrap.cpp를 작성해서 실제 컴파일러(g++ -std=c++23 -fsyntax-only)로
    /// 그 저장소의 헤더에 대고 문법 검증까지 통과시켰습니다. 아래 DllImport들은
    /// 그 sc55_wrap.cpp가 export하는 함수와 정확히 대응합니다.
    ///
    /// 준비물:
    ///  1. nuked_sc55_wrap.dll(x64) → Native/x64/ 에 배치
    ///     (linoshkmalayil/Nuked-SC55-GUI-Float 소스에 이미 추가되어 있는
    ///     sc55_wrap 타깃을 CMake로 빌드하면 나옵니다: src/csharp_wrap/README.md 참고.
    ///     SDL2/rtmidi 필요 없이 이 타깃 하나만 따로 빌드 가능합니다.)
    ///  2. SPE5 ROM 세트(실기 덤프) → RomDirectory에 폴더 지정
    ///     (이 프로젝트의 Roms\SC55mk2\ 에 이미 rom1.bin/rom2.bin/rom_sm.bin/
    ///     waverom1.bin/waverom2.bin 표준 파일명으로 정리되어 있습니다)
    /// </summary>
    public sealed class Sc55Engine : ISynthEngine
    {
        /// <summary>
        /// nukeykt/Nuked-SC55 공식 README가 명시하는, SPE5/SC-155mkII(v1.01) 세트의
        /// 정확한 파일명 5개입니다. 칩 번호까지 못박혀 있어서(R15199858→rom1.bin 등)
        /// 확실합니다. RomDirectory 폴더 안에 이 이름 그대로 있어야 합니다.
        /// </summary>
        public static readonly string[] RequiredRomFiles =
            { "rom1.bin", "rom2.bin", "rom_sm.bin", "waverom1.bin", "waverom2.bin" };

        private const string Lib = "nuked_sc55_wrap";

        [DllImport(Lib)] private static extern IntPtr sc55_create([MarshalAs(UnmanagedType.LPStr)] string romDirectory);
        [DllImport(Lib)] private static extern void sc55_destroy(IntPtr instance);
        [DllImport(Lib)] private static extern void sc55_feed_midi(IntPtr instance, byte[] data, int length);
        [DllImport(Lib)] private static extern void sc55_render_float(IntPtr instance, float[] buffer, int frameCount);
        [DllImport(Lib)] private static extern int sc55_get_lcd_contrast(IntPtr instance);
        [DllImport(Lib)] private static extern void sc55_set_lcd_contrast(IntPtr instance, int contrast);
        [DllImport(Lib)] private static extern void sc55_set_buttons(IntPtr instance, uint mask, uint pressed);
        [DllImport(Lib)] private static extern void sc55_set_button(IntPtr instance, int button, int pressed);
        [DllImport(Lib)] private static extern int sc55_get_lcd(IntPtr instance, uint[] outBgra, int capacityPixels, out int width, out int height);

        // Nuked-SC55는 LCD를 문자열이 아니라 도트 비트맵으로 렌더링하는 원본 방식이라,
        // 래퍼에서 "지금 활성 패치명" 정도만 문자열로 뽑아주는 형태를 가정합니다.
        [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int sc55_get_status_text(IntPtr instance, byte[] outBuffer, int bufferSize);
        [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int sc55_get_last_error(byte[] outBuffer, int bufferSize);
        [DllImport(Lib)] private static extern void sc55_set_boot_steps(ulong steps);

        /// <summary>
        /// 실기 펌웨어 부팅에 돌릴 에뮬레이션 스텝 수. 0이면 네이티브 기본값(1200만)을 씁니다.
        ///
        /// 왜 이런 게 필요한가: SPE5는 전원을 켜고 펌웨어가 다 올라와야 MIDI에 반응합니다.
        /// 이 과정을 건너뛰면 ROM을 제대로 읽어도 "아무 소리도 안 나는" 상태가 됩니다.
        /// 실측으로 400만 스텝은 무음, 800만부터 정상 출력이었고, 기본값은 안전 마진을
        /// 둔 1200만입니다(PC에서 약 3초). 원본 렌더러는 2400만을 씁니다.
        /// </summary>
        public ulong BootSteps { get; set; } = 0;

        /// <summary>
        /// 마지막 sc55_create 호출의 결과 메시지. 실패했을 때 어느 단계에서
        /// 걸렸는지(ROM 인식 / 세트 완성도 / 적재 / 초기화) 알려줍니다.
        /// </summary>
        public static string GetLastNativeMessage()
        {
            try
            {
                var buf = new byte[512];
                int len = sc55_get_last_error(buf, buf.Length);
                return len > 0 ? System.Text.Encoding.UTF8.GetString(buf, 0, len) : "";
            }
            catch
            {
                return "";
            }
        }

        private IntPtr _instance;

        // 네이티브 래퍼 안에도 뮤텍스가 있지만, C# 쪽에서 _instance 자체가
        // Dispose로 0이 되는 순간과 겹치면 이미 해제된 포인터로 호출하게 됩니다
        // (use-after-free → 앱이 즉시 죽음). 그래서 이쪽에서도 직렬화합니다.
        private readonly object _sync = new();

        public string Name => "SPE5 (Nuked-SC55)";
        public bool HandlesDeviceInquiry => true; // 실기 펌웨어를 그대로 돌리므로 표준 GS Device Inquiry에 응답
        public event EventHandler<string>? LcdTextChanged;

        public string RomDirectory { get; set; } = "";

        public void Open()
        {
            var missing = Array.FindAll(RequiredRomFiles,
                name => !System.IO.File.Exists(System.IO.Path.Combine(RomDirectory, name)));
            if (missing.Length > 0)
                throw new InvalidOperationException(
                    $"SPE5 ROM 파일이 없습니다: {string.Join(", ", missing)} (경로: {RomDirectory})");

            if (BootSteps > 0) sc55_set_boot_steps(BootSteps);

            // 이 호출은 펌웨어 부팅 때문에 몇 초 걸립니다(동기 호출).
            _instance = sc55_create(RomDirectory);
            if (_instance == IntPtr.Zero)
            {
                string detail = GetLastNativeMessage();
                throw new InvalidOperationException(
                    $"SPE5 ROM 로드 실패 - {(string.IsNullOrEmpty(detail) ? "원인 불명" : detail)} (경로: {RomDirectory})");
            }
            LcdTextChanged?.Invoke(this, "SPE5  Ready.");
        }

        private void Feed(params byte[] bytes)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                WakeUp();
                sc55_feed_midi(_instance, bytes, bytes.Length);
            }
            // 여기서 화면 텍스트를 읽지 않습니다. 예전에는 메시지마다 읽었는데,
            // 빠른 패시지에서는 초당 수백 번 P/Invoke가 일어나 오디오가 끊길 수 있습니다.
            // 대신 MainWindow가 타이머로 PollStatusText()를 주기적으로 부릅니다.
        }

        public void NoteOn(int channel, int note, int velocity) => Feed((byte)(0x90 | channel), (byte)note, (byte)velocity);
        public void NoteOff(int channel, int note, int velocity) => Feed((byte)(0x80 | channel), (byte)note, (byte)velocity);
        public void ControlChange(int channel, int controller, int value) => Feed((byte)(0xB0 | channel), (byte)controller, (byte)value);
        public void ProgramChange(int channel, int program) => Feed((byte)(0xC0 | channel), (byte)program);
        public void PitchBend(int channel, int value14bit) => Feed((byte)(0xE0 | channel), (byte)(value14bit & 0x7F), (byte)((value14bit >> 7) & 0x7F));
        public void SysEx(ReadOnlySpan<byte> data) => Feed(data.ToArray());

        // ---- 유휴 절전 ----
        // SPE5 에뮬레이터는 사이클 단위로 실기 CPU를 흉내내서, 아무 소리도 안 내는 중에도
        // 콜백 예산(10ms)의 약 40%를 먹습니다. 그래서 "출력이 계속 무음이고 MIDI 입력도 없으면"
        // 에뮬레이션 자체를 멈추고(시간이 정지할 뿐이라 상태는 그대로), MIDI가 들어오는 즉시 재개합니다.
        // 무음 판정은 음량 기준이라 리버브/릴리즈 꼬리가 끝난 뒤에만 멈춥니다.
        private const float IdleThreshold = 1e-4f;       // 약 -80dB
        private const int IdleAfterFrames = 44100 * 2;    // 2초 연속 무음
        // MIDI를 받은 뒤 이 시간 동안은 소리가 없어도 에뮬레이터를 계속 돌린다. 펌웨어의 시간 기반 동작
        // (GS 표시 메시지가 몇 초 뒤 원래 화면으로 돌아가기, 미터 감쇠 등)이 실기처럼 끝까지 진행되게 하려는 것.
        // 에뮬레이터를 멈추면 그 시간도 같이 멈춰서, 표시 메시지가 영원히 남는 문제가 있었다.
        private const long KeepAwakeAfterMidiMs = 8000;
        private int _silentFrames;
        private bool _idle;
        private long _lastMidiTick;

        /// <summary>[진단용] 지금 에뮬레이션을 멈추고 쉬는 중인지.</summary>
        public bool IsIdle { get { lock (_sync) return _idle; } }

        // 반드시 _sync 안에서 호출
        private void WakeUp()
        {
            _idle = false;
            _silentFrames = 0;
            _lastMidiTick = Environment.TickCount64;
        }

        public void RenderFloat(float[] buffer, int frameCount)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero || _idle)
                {
                    Array.Clear(buffer, 0, frameCount * 2); // 무음으로 채워줍니다
                    return;
                }
                sc55_render_float(_instance, buffer, frameCount);

                float peak = 0f;
                int n = frameCount * 2;
                for (int i = 0; i < n; i++)
                {
                    float a = buffer[i] < 0 ? -buffer[i] : buffer[i];
                    if (a > peak) peak = a;
                }
                if (peak > IdleThreshold) _silentFrames = 0;
                else
                {
                    _silentFrames = Math.Min(_silentFrames + frameCount, IdleAfterFrames);
                    if (_silentFrames >= IdleAfterFrames && Environment.TickCount64 - _lastMidiTick >= KeepAwakeAfterMidiMs)
                        _idle = true;
                }
            }
        }

        // LCD 픽셀은 오디오 렌더링(_sync)과 따로 잠근다: 화면 그리기 때문에 오디오 스레드가 기다리면 소리가 끊긴다.
        // (네이티브 쪽도 LCD_Render가 자기 뮤텍스를 try_lock으로만 잡아 오디오를 막지 않는다.)
        private readonly object _lcdSync = new();
        private bool _lcdSupported = true;

        /// <summary>
        /// 에뮬레이터가 그린 실제 LCD 화면(SPE5는 741x268)을 BGRA 픽셀로 받아옵니다.
        /// 부팅 배너, 펌웨어가 만드는 파트/악기 표시, 레벨 미터까지 실기 그대로입니다.
        /// 엔진이 준비 안 됐거나 DLL이 이 기능을 모르면(옛 DLL) false를 돌려주니, 호출한 쪽은 기존 화면으로 대체하면 됩니다.
        /// </summary>
        public bool TryReadLcd(uint[] dest, out int width, out int height)
        {
            width = height = 0;
            lock (_lcdSync)
            {
                if (_instance == IntPtr.Zero || !_lcdSupported) return false;
                try
                {
                    return sc55_get_lcd(_instance, dest, dest.Length, out width, out height) != 0;
                }
                catch (EntryPointNotFoundException)
                {
                    _lcdSupported = false; // 옛 DLL: 다시 시도하지 않는다
                    return false;
                }
            }
        }

        /// <summary>
        /// 실기 전면 버튼을 누르거나 뗍니다. button은 에뮬레이터의 MCU_BUTTON_* 번호(예: PART ◀ = 22, INSTRUMENT ▶ = 4).
        /// 펌웨어가 실제 버튼처럼 반응하므로 파트 선택, 값 조정 등이 실기대로 동작합니다.
        /// 유휴 절전 중이면 깨웁니다(멈춘 에뮬레이터는 버튼을 읽지 못한다).
        /// </summary>
        public void SetButton(int button, bool pressed)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                WakeUp();
                try { sc55_set_button(_instance, button, pressed ? 1 : 0); }
                catch (EntryPointNotFoundException) { /* 옛 DLL: 버튼 기능 없음 */ }
            }
        }

        /// <summary>
        /// 여러 버튼을 같은 순간에 누르거나 뗍니다(mask에 든 번호만 pressed 값으로 덮어씀). PART [◀]+[▶] 같은 동시 누름용.
        /// 하나씩 SetButton을 두 번 부르면 그 사이 오디오 렌더링이 끼어들어 펌웨어가 먼저 온 버튼만 처리해 버립니다.
        /// </summary>
        public void SetButtons(uint mask, uint pressed)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                WakeUp();
                try { sc55_set_buttons(_instance, mask, pressed); }
                catch (EntryPointNotFoundException) { }
            }
        }

        /// <summary>LCD 대비(1~16). 엔진이 준비 안 됐거나 옛 DLL이면 0.</summary>
        public int GetLcdContrast()
        {
            lock (_lcdSync)
            {
                if (_instance == IntPtr.Zero) return 0;
                try { return sc55_get_lcd_contrast(_instance); }
                catch (EntryPointNotFoundException) { return 0; }
            }
        }

        /// <summary>LCD 대비를 정한다(1~16으로 잘림). 앱이 저장해 둔 값을 부팅 뒤에 되돌릴 때 쓴다.</summary>
        public void SetLcdContrast(int contrast)
        {
            lock (_lcdSync)
            {
                if (_instance == IntPtr.Zero) return;
                try { sc55_set_lcd_contrast(_instance, contrast); }
                catch (EntryPointNotFoundException) { }
            }
        }

        private string _lastStatusText = "";

        /// <summary>
        /// 실기 LCD에 표시된 텍스트를 읽어옵니다. UI 갱신 주기(수십~수백 ms)마다
        /// 호출하세요 - MIDI 메시지마다 부르면 P/Invoke 비용 때문에 소리가 끊깁니다.
        /// 값이 실제로 바뀌었을 때만 LcdTextChanged를 올립니다.
        /// </summary>
        public void PollStatusText()
        {
            var buf = new byte[128];
            int len;
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                len = sc55_get_status_text(_instance, buf, buf.Length);
            }
            if (len <= 0) return;

            string text = System.Text.Encoding.ASCII.GetString(buf, 0, len).TrimEnd();
            if (text == _lastStatusText) return;
            _lastStatusText = text;
            LcdTextChanged?.Invoke(this, text);
        }

        public void Dispose()
        {
            // 락 순서: _sync -> _lcdSync (다른 곳에서는 두 락을 함께 잡지 않으므로 교착이 생기지 않는다)
            lock (_sync)
            lock (_lcdSync)
            {
                if (_instance == IntPtr.Zero) return;
                // 참조를 먼저 0으로 만든 뒤 해제합니다. 락 덕분에 렌더링/입력/LCD 읽기가
                // 이 사이에 끼어들 수 없고, 이후 호출은 조용히 무시됩니다.
                var handle = _instance;
                _instance = IntPtr.Zero;
                sc55_destroy(handle);
            }
        }
    }
}
