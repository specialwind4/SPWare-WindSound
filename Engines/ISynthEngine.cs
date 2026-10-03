using System;

namespace SPWare.VirtualSoundCanvas.Engines
{
    /// <summary>
    /// 모든 신스 엔진(MTS3/Munt, SC-88Pro 등)이 구현해야 하는 공통 계약.
    /// 라우터와 UI는 이 인터페이스만 알고 있으면 되고, 실제 엔진 구현체는
    /// 언제든 교체 가능합니다. (지금은 SC-88Pro 자리에 ExternalForwardingEngine이
    /// 들어가 있고, 나중에 진짜 SC-88Pro 엔진이 준비되면 이 인터페이스를
    /// 구현하는 새 클래스로 갈아끼우면 됩니다.)
    /// </summary>
    public interface ISynthEngine : IDisposable
    {
        /// <summary>엔진 표시 이름 (UI, 로그용)</summary>
        string Name { get; }

        /// <summary>
        /// 이 엔진이 "장치 조회(Device Inquiry)"에 직접 응답할 수 있는지 여부.
        /// false면 MidiRouter가 대신 정책에 따라 응답하거나 무시합니다.
        /// </summary>
        bool HandlesDeviceInquiry { get; }

        /// <summary>엔진 초기화 (ROM 로드, 외부 프로세스 연결 등)</summary>
        void Open();

        void NoteOn(int channel, int note, int velocity);
        void NoteOff(int channel, int note, int velocity);
        void ControlChange(int channel, int controller, int value);
        void ProgramChange(int channel, int program);
        void PitchBend(int channel, int value14bit);

        /// <summary>SysEx 전체 바이트열 (F0 ... F7 포함)</summary>
        void SysEx(ReadOnlySpan<byte> data);

        /// <summary>
        /// 오디오 프레임 렌더링. 스테레오 인터리브 float, [-1, 1] 범위.
        /// ExternalForwardingEngine처럼 자체적으로 소리를 내는 엔진은
        /// 이 메서드를 비워두고 buffer를 채우지 않아도 됩니다(무음 반환).
        /// </summary>
        void RenderFloat(float[] buffer, int frameCount);

        /// <summary>
        /// LCD에 표시할 텍스트가 갱신되었을 때 발생.
        /// (Munt는 SysEx로 들어온 메시지/패치명 변경 시 이 이벤트를 올려줍니다.)
        /// </summary>
        event EventHandler<string>? LcdTextChanged;
    }
}
