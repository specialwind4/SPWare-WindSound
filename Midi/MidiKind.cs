using System;

namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>
    /// 지금 들어오는 MIDI가 어느 "종류"인지. 종류가 정해지면 채널 배정과 기본 엔진이 한 번에 정해진다.
    /// </summary>
    public enum MidiKind
    {
        /// <summary>직접 설정: 채널 배정과 기본 엔진을 사용자가 손으로 관리한다(자동으로 건드리지 않음).</summary>
        Custom = 0,
        /// <summary>MTS3용 음악(게임의 MTS3 트랙 등). 모든 채널을 MTS3 에뮬레이터로.</summary>
        Mt32 = 1,
        /// <summary>GM / GS 음악. 모든 채널을 MTS5 에뮬레이터로.</summary>
        GmGs = 2,
    }

    /// <summary>들어오는 SysEx로 MIDI 종류를 알아채는 감지기.</summary>
    public static class MidiKindDetector
    {
        /// <summary>
        /// SysEx 한 개를 보고 종류를 추정한다. 알아볼 수 없으면 <see cref="MidiKind.Custom"/>을 돌려준다(= 판단 안 함).
        ///
        /// 알아보는 신호:
        ///  - GM System On / GM2 System On : F0 7E dev 09 01|03 F7   -> GM/GS
        ///  - GS 계열 SPWare SysEx        : F0 41 dev 42 ...        -> GM/GS (GS Reset 포함)
        ///  - MTS3 계열 SPWare SysEx      : F0 41 dev 16 ...        -> MTS3
        ///
        /// 한계: 이런 초기화 신호를 아예 보내지 않는 프로그램(예: 원숭이섬)은 감지할 수 없다.
        /// 그런 경우엔 화면에서 MIDI 종류를 직접 골라야 한다.
        /// </summary>
        public static MidiKind Detect(ReadOnlySpan<byte> d)
        {
            if (d.Length < 6 || d[0] != 0xF0) return MidiKind.Custom;

            // 유니버설 Non-Realtime: GM System On (09 01) / GM2 System On (09 03)
            if (d[1] == 0x7E && d[3] == 0x09 && (d[4] == 0x01 || d[4] == 0x03))
                return MidiKind.GmGs;

            // SPWare(41): 모델 ID로 구분한다. 0x42 = GS, 0x16 = MTS3
            if (d[1] == 0x41 && d.Length >= 8)
            {
                if (d[3] == 0x42) return MidiKind.GmGs;
                if (d[3] == 0x16) return MidiKind.Mt32;
            }

            return MidiKind.Custom;
        }

        public static string Describe(MidiKind kind) => kind switch
        {
            MidiKind.Mt32 => "MTS3",
            MidiKind.GmGs => "GM / GS (MTS5)",
            _ => "직접 설정",
        };
    }
}
