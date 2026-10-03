namespace SPWare.VirtualSoundCanvas.Midi
{
    public enum MidiMessageType
    {
        NoteOff, NoteOn, ControlChange, ProgramChange, PitchBend, SysEx, Other
    }

    /// <summary>3바이트 이하의 짧은 MIDI 메시지 하나를 표현.</summary>
    public readonly struct MidiMessage
    {
        public MidiMessageType Type { get; }
        public int Channel { get; }   // 0-15
        public int Data1 { get; }
        public int Data2 { get; }

        private MidiMessage(MidiMessageType type, int channel, int d1, int d2)
        {
            Type = type; Channel = channel; Data1 = d1; Data2 = d2;
        }

        public static MidiMessage Parse(byte status, byte data1, byte data2)
        {
            int channel = status & 0x0F;
            return (status & 0xF0) switch
            {
                0x80 => new MidiMessage(MidiMessageType.NoteOff, channel, data1, data2),
                0x90 => data2 == 0
                    ? new MidiMessage(MidiMessageType.NoteOff, channel, data1, data2)
                    : new MidiMessage(MidiMessageType.NoteOn, channel, data1, data2),
                0xB0 => new MidiMessage(MidiMessageType.ControlChange, channel, data1, data2),
                0xC0 => new MidiMessage(MidiMessageType.ProgramChange, channel, data1, 0),
                0xE0 => new MidiMessage(MidiMessageType.PitchBend, channel, data1 | (data2 << 7), 0),
                _ => new MidiMessage(MidiMessageType.Other, channel, data1, data2),
            };
        }
    }
}
