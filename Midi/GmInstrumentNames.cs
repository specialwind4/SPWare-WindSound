namespace SPWare.VirtualSoundCanvas.Midi
{
    /// <summary>
    /// General MIDI Level 1 기준 128개 악기 이름 + 표준 드럼 세트 이름.
    ///
    /// 주의: 이건 "Bank 0(기본)" 기준 GM 호환 이름입니다. 실제 SC-88Pro는
    /// Bank Select(CC0/CC32)로 같은 프로그램 번호 안에서도 수백 개의 배리에이션
    /// 톤(Variation Tone)을 추가로 갖고 있어서(총 800여 개), 진짜 실기와 100%
    /// 동일한 이름을 보여주려면 그 전체 배리에이션 테이블이 따로 필요합니다.
    /// 지금은 Bank 0 기준 128개 기본 이름만 다룹니다.
    /// </summary>
    public static class GmInstrumentNames
    {
        public static readonly string[] Melodic =
        {
            // 1-8 Piano
            "Acoustic Grand Piano", "Bright Acoustic Piano", "Electric Grand Piano", "Honky-tonk Piano",
            "Electric Piano 1", "Electric Piano 2", "Harpsichord", "Clavi",
            // 9-16 Chromatic Percussion
            "Celesta", "Glockenspiel", "Music Box", "Vibraphone",
            "Marimba", "Xylophone", "Tubular Bells", "Dulcimer",
            // 17-24 Organ
            "Drawbar Organ", "Percussive Organ", "Rock Organ", "Church Organ",
            "Reed Organ", "Accordion", "Harmonica", "Tango Accordion",
            // 25-32 Guitar
            "Acoustic Guitar (nylon)", "Acoustic Guitar (steel)", "Electric Guitar (jazz)", "Electric Guitar (clean)",
            "Electric Guitar (muted)", "Overdriven Guitar", "Distortion Guitar", "Guitar Harmonics",
            // 33-40 Bass
            "Acoustic Bass", "Electric Bass (finger)", "Electric Bass (pick)", "Fretless Bass",
            "Slap Bass 1", "Slap Bass 2", "Synth Bass 1", "Synth Bass 2",
            // 41-48 Strings
            "Violin", "Viola", "Cello", "Contrabass",
            "Tremolo Strings", "Pizzicato Strings", "Orchestral Harp", "Timpani",
            // 49-56 Ensemble
            "String Ensemble 1", "String Ensemble 2", "Synth Strings 1", "Synth Strings 2",
            "Choir Aahs", "Voice Oohs", "Synth Voice", "Orchestra Hit",
            // 57-64 Brass
            "Trumpet", "Trombone", "Tuba", "Muted Trumpet",
            "French Horn", "Brass Section", "Synth Brass 1", "Synth Brass 2",
            // 65-72 Reed
            "Soprano Sax", "Alto Sax", "Tenor Sax", "Baritone Sax",
            "Oboe", "English Horn", "Bassoon", "Clarinet",
            // 73-80 Pipe
            "Piccolo", "Flute", "Recorder", "Pan Flute",
            "Blown Bottle", "Shakuhachi", "Whistle", "Ocarina",
            // 81-88 Synth Lead
            "Lead 1 (square)", "Lead 2 (sawtooth)", "Lead 3 (calliope)", "Lead 4 (chiff)",
            "Lead 5 (charang)", "Lead 6 (voice)", "Lead 7 (fifths)", "Lead 8 (bass + lead)",
            // 89-96 Synth Pad
            "Pad 1 (new age)", "Pad 2 (warm)", "Pad 3 (polysynth)", "Pad 4 (choir)",
            "Pad 5 (bowed)", "Pad 6 (metallic)", "Pad 7 (halo)", "Pad 8 (sweep)",
            // 97-104 Synth Effects
            "FX 1 (rain)", "FX 2 (soundtrack)", "FX 3 (crystal)", "FX 4 (atmosphere)",
            "FX 5 (brightness)", "FX 6 (goblins)", "FX 7 (echoes)", "FX 8 (sci-fi)",
            // 105-112 Ethnic
            "Sitar", "Banjo", "Shamisen", "Koto",
            "Kalimba", "Bag pipe", "Fiddle", "Shanai",
            // 113-120 Percussive
            "Tinkle Bell", "Agogo", "Steel Drums", "Woodblock",
            "Taiko Drum", "Melodic Tom", "Synth Drum", "Reverse Cymbal",
            // 121-128 Sound Effects
            "Guitar Fret Noise", "Breath Noise", "Seashore", "Bird Tweet",
            "Telephone Ring", "Helicopter", "Applause", "Gunshot",
        };

        /// <summary>
        /// SC-88Pro 공식 Owner's Manual(p.163 Drum set list)의 Native Map 드럼 세트
        /// 25종을 그대로 옮긴 표. 드럼 채널(보통 MIDI 채널 10, 0-based 9)의 프로그램 번호에
        /// 대응합니다.
        /// </summary>
        public static readonly (int Program, string Name)[] DrumKits =
        {
            (0, "STANDARD 1"),
            (1, "STANDARD 2"),
            (2, "STANDARD 3"),
            (8, "ROOM"),
            (9, "HIP HOP"),
            (10, "JUNGLE"),
            (11, "TECHNO"),
            (16, "POWER"),
            (24, "ELECTRONIC"),
            (25, "TR-808"),
            (26, "DANCE"),
            (27, "CR-78"),
            (28, "TR-606"),
            (29, "TR-707"),
            (30, "TR-909"),
            (32, "JAZZ"),
            (40, "BRUSH"),
            (48, "ORCHESTRA"),
            (49, "ETHNIC"),
            (50, "KICK & SNARE"),
            (52, "ASIA"),
            (53, "CYMBAL&CLAPS"),
            (56, "SFX"),
            (57, "RHYTHM FX"),
            (58, "RHYTHM FX 2"),
        };

        public static string GetMelodicName(int program) =>
            program is >= 0 and < 128 ? Melodic[program] : $"Prog #{program:000}";

        public static string GetDrumKitName(int program)
        {
            string name = "STANDARD 1";
            foreach (var (p, n) in DrumKits)
                if (program >= p) name = n; // 가장 가까운 하한값의 세트 이름 사용
            return name;
        }
    }
}
