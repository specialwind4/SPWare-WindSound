using System;
using System.Runtime.InteropServices;

namespace SPWare.VirtualSoundCanvas.Engines
{
    /// <summary>실기 ROM 세트 3종 - 게임 호환성이 셋 다 다릅니다.</summary>
    public enum Mt32RomSet
    {
        Mt32Old,
        Mt32New,
        Cm32L,
    }

    /// <summary>
    /// MTS3 / MTS3L 엔진. Munt.NET(C# 래퍼) 대신, mt32emu(munt/munt) 본체를
    /// 직접 감싼 얇은 C 래퍼(mt32_wrap.dll)를 씁니다.
    ///
    /// 왜 Munt.NET을 버렸는가: 리플렉션으로 Munt.NET의 실제 공개 메서드를 전수
    /// 확인해보니(LoadRoms/SetSampleRate/Open/PlayMsg/PlaySysex/Render/Dispose 7개)
    /// LCD 텍스트를 읽어오는 기능이 아예 없었습니다. 반면 mt32-pi(라즈베리파이용
    /// 실물 MTS3 에뮬레이터, 물리 LCD에 음색명을 실제로 표시함)의 소스를 보면
    /// mt32emu 본체에 직접 이렇게 호출합니다:
    ///
    ///   m_pSynth->getDisplayState(m_LCDTextBuffer, bNarrowPartStateText);
    ///
    /// 그래서 MTS5Engine과 같은 패턴으로 mt32emu 소스(munt/munt) 위에 우리만의
    /// C 래퍼(src/csharp_wrap/mt32_wrap.cpp)를 새로 작성했습니다. 실제로 링크·실행
    /// 까지 검증했고, LCD 텍스트가 실기 사진과 동일한 형식으로 나오는 것까지
    /// 확인했습니다("** SPWare MTS3 **" → 노트 연주 시 "1 2 3 4 5 R |vol:100").
    ///
    /// 알아두어야 할 것 - MTS3의 유명한 함정: 실기는 기본 상태에서 MIDI 채널
    /// 1번(0-based 0)에 어느 파트도 배정되어 있지 않습니다(공장 출하 패치가
    /// 채널 2~9번에 파트 1~8을, 10번에 리듬을 배정). 채널 1번으로 노트를 보내면
    /// "정상적으로 아무 반응도 없습니다" - 버그가 아니라 실기 그대로의 동작입니다.
    /// 채널 2번(0-based 1) 이상을 쓰거나, GS/GM 리셋 SysEx 이후 재배정하세요.
    ///
    /// 준비물:
    ///  1. mt32_wrap.dll(x64) → Native/x64/ 에 배치
    ///     (munt/munt 저장소의 mt32emu 폴더에 추가된 mt32_wrap 타깃을 CMake로 빌드)
    ///     cmake -B build -G "Visual Studio 17 2022" -A x64
    ///     cmake --build build --config Release --target mt32_wrap
    ///  2. MTS3/MTS3L ROM 세트(실기 덤프) → ControlRomPath/PcmRomPath에 지정
    /// </summary>
    public sealed class Mt32Engine : ISynthEngine
    {
        private const string Lib = "mt32_wrap";

        [DllImport(Lib)] private static extern IntPtr mt32_create(
            [MarshalAs(UnmanagedType.LPStr)] string controlRomPath,
            [MarshalAs(UnmanagedType.LPStr)] string pcmRomPath);
        [DllImport(Lib)] private static extern void mt32_destroy(IntPtr instance);
        [DllImport(Lib)] private static extern void mt32_play_short(IntPtr instance, uint packedMsg);
        [DllImport(Lib)] private static extern void mt32_play_sysex(IntPtr instance, byte[] data, int length);
        [DllImport(Lib)] private static extern void mt32_render_float(IntPtr instance, float[] buffer, int frameCount);
        [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int mt32_get_display_text(IntPtr instance, byte[] outBuffer, int bufferSize);
        [DllImport(Lib, CharSet = CharSet.Ansi)] private static extern int mt32_get_last_error(byte[] outBuffer, int bufferSize);

        private IntPtr _instance;
        private readonly object _sync = new();
        private string _lastDisplayText = "";

        public string Name => "MTS3 (mt32emu)";
        public bool HandlesDeviceInquiry => true;
        public event EventHandler<string>? LcdTextChanged;

        public Mt32RomSet RomSet { get; set; } = Mt32RomSet.Mt32Old;
        public string ControlRomPath { get; set; } = "";
        public string PcmRomPath { get; set; } = "";

        public static string GetLastNativeMessage()
        {
            try
            {
                var buf = new byte[512];
                int len = mt32_get_last_error(buf, buf.Length);
                return len > 0 ? System.Text.Encoding.ASCII.GetString(buf, 0, len) : "";
            }
            catch
            {
                return "";
            }
        }

        public void Open()
        {
            lock (_sync)
            {
                _instance = mt32_create(ControlRomPath, PcmRomPath);
                if (_instance == IntPtr.Zero)
                {
                    string detail = GetLastNativeMessage();
                    throw new InvalidOperationException(
                        $"MTS3 ROM 로드 실패 - {(string.IsNullOrEmpty(detail) ? "원인 불명" : detail)} " +
                        $"(CONTROL: {ControlRomPath}, PCM: {PcmRomPath})");
                }
            }
        }

        private void PlayShort(int status, int d1, int d2)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                mt32_play_short(_instance, (uint)(status | (d1 << 8) | (d2 << 16)));
            }
        }

        public void NoteOn(int channel, int note, int velocity) => PlayShort(0x90 | channel, note, velocity);
        public void NoteOff(int channel, int note, int velocity) => PlayShort(0x80 | channel, note, velocity);
        public void ControlChange(int channel, int controller, int value) => PlayShort(0xB0 | channel, controller, value);
        public void ProgramChange(int channel, int program) => PlayShort(0xC0 | channel, program, 0);
        public void PitchBend(int channel, int value14bit) =>
            PlayShort(0xE0 | channel, value14bit & 0x7F, (value14bit >> 7) & 0x7F);

        public void SysEx(ReadOnlySpan<byte> data)
        {
            if (data.Length == 0) return;
            var bytes = data.ToArray();
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                mt32_play_sysex(_instance, bytes, bytes.Length);
            }
        }

        /// <summary>
        /// MTS3를 초기 상태로 되돌린다(Munt의 리셋 SysEx: 주소 7F 00 00, 데이터 없음).
        /// 다른 게임이 남긴 음색/파트 설정이 지워진다. 마스터 볼륨은 호출한 쪽에서 다시 맞춘다.
        /// </summary>
        public void Reset() =>
            SysEx(new byte[] { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x7F, 0x00, 0x00, 0x01, 0xF7 });

        /// <summary>
        /// MTS3 마스터 볼륨(0~100)을 실기와 같은 방식으로(시스템 영역 SysEx, 주소 10 00 16) 바꾼다.
        /// 액정의 Vol 값도 이 값을 따라 바뀐다.
        /// </summary>
        public void SetMasterVolume(int volume)
        {
            byte v = (byte)Math.Clamp(volume, 0, 100);
            int sum = 0x10 + 0x00 + 0x16 + v;
            byte checksum = (byte)((128 - (sum % 128)) % 128);
            SysEx(new byte[] { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x10, 0x00, 0x16, v, checksum, 0xF7 });
        }

        /// <summary>
        /// 파트(1~8) 또는 리듬 파트(9)의 음량(0~100)을 바꾼다. 실기의 PART -> VOLUME -> 노브 순서와 같은 값
        /// (Patch Temporary Area의 OUTPUT LEVEL, 매뉴얼 "5-2 Patch temporary area" / "5-5 System area" 주소 맵).
        /// 파트 1~8은 03 00 (part-1)*10 + 08, 리듬 파트는 03 01 08.
        /// </summary>
        public void SetPartVolume(int part, int volume)
        {
            byte v = (byte)Math.Clamp(volume, 0, 100);
            byte a1, a2;
            if (part == 9) { a1 = 0x01; a2 = 0x08; }
            else { a1 = 0x00; a2 = (byte)(((Math.Clamp(part, 1, 8) - 1) * 0x10) + 0x08); }
            int sum = 0x03 + a1 + a2 + v;
            byte checksum = (byte)((128 - (sum % 128)) % 128);
            SysEx(new byte[] { 0xF0, 0x41, 0x10, 0x16, 0x12, 0x03, a1, a2, v, checksum, 0xF7 });
        }

        /// <summary>
        /// 액정에 우리가 만든 20자 문구를 띄운다(디스플레이 메모리 영역, 주소 20 00 00).
        /// 실기 버튼(PART/VOLUME 등)을 누르면 액정에 뜨는 안내를 흉내 내는 용도 - Munt는 그 안내 자체는
        /// 만들어 주지 않으므로(버튼 반응은 구현 범위 밖) 우리가 직접 문구를 계산해서 보낸다.
        /// </summary>
        public void ShowCustomMessage(string text)
        {
            text = (text ?? "").PadRight(20);
            if (text.Length > 20) text = text.Substring(0, 20);
            byte[] chars = System.Text.Encoding.ASCII.GetBytes(text);
            int sum = 0x20 + 0x00 + 0x00;
            foreach (byte c in chars) sum += c;
            byte checksum = (byte)((128 - (sum % 128)) % 128);

            byte[] msg = new byte[5 + 3 + chars.Length + 2];
            int i = 0;
            msg[i++] = 0xF0; msg[i++] = 0x41; msg[i++] = 0x10; msg[i++] = 0x16; msg[i++] = 0x12;
            msg[i++] = 0x20; msg[i++] = 0x00; msg[i++] = 0x00;
            Array.Copy(chars, 0, msg, i, chars.Length); i += chars.Length;
            msg[i++] = checksum;
            msg[i] = 0xF7;
            SysEx(msg);
        }

        public void RenderFloat(float[] buffer, int frameCount)
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero)
                {
                    Array.Clear(buffer, 0, frameCount * 2);
                    return;
                }
                mt32_render_float(_instance, buffer, frameCount);
            }
        }

        /// <summary>
        /// 실기 LCD 텍스트를 읽어옵니다. UI 갱신 주기(수십~수백 ms)마다 호출하세요 -
        /// MIDI 메시지마다 부르면 P/Invoke 비용 때문에 소리가 끊깁니다.
        /// </summary>
        public void PollDisplayState()
        {
            byte[] buf = new byte[64];
            int len;
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                len = mt32_get_display_text(_instance, buf, buf.Length);
            }
            if (len <= 0) return;

            string text = System.Text.Encoding.ASCII.GetString(buf, 0, len).TrimEnd();
            if (text == _lastDisplayText) return;
            _lastDisplayText = text;
            LcdTextChanged?.Invoke(this, text);
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_instance == IntPtr.Zero) return;
                var handle = _instance;
                _instance = IntPtr.Zero;
                mt32_destroy(handle);
            }
        }
    }
}
