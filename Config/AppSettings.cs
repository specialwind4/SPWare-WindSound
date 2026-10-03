using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SPWare.VirtualSoundCanvas.Config
{
    /// <summary>
    /// 앱 설정. %AppData%\SPWare\VirtualSoundCanvas\settings.json 에 저장됩니다.
    ///
    /// ROM 경로를 설정으로 뺀 이유가 두 가지입니다.
    ///  1) ROM은 저작물이라 프로그램 폴더에 같이 배포할 성격이 아닙니다.
    ///  2) 프로젝트 폴더 안에 두면 빌드할 때마다 복사가 얽혀서 문제가 생기기 쉽습니다
    ///     (실제로 publish 폴더에서 PCM 롬이 사라지는 문제를 겪었습니다).
    /// 사용자가 원하는 아무 폴더에나 ROM을 두고 그 경로를 지정하는 쪽이 훨씬 안전합니다.
    /// </summary>
    public sealed class AppSettings
    {
        // ---- ROM 경로 ----
        public string Mt32OldRomFolder { get; set; } = "";
        public string Mt32NewRomFolder { get; set; } = "";
        public string Cm32LRomFolder { get; set; } = "";
        public string Sc55RomFolder { get; set; } = "";

        // ---- 선택 상태 ----
        /// <summary>0=구형 SPE3, 1=신형 SPE3, 2=SPE3L</summary>
        public int Mt32RomSetIndex { get; set; } = 0;

        /// <summary>MIDI 장치는 인덱스가 아니라 이름으로 저장합니다 - USB 장치를 꽂고 빼면
        /// 인덱스가 밀려서 엉뚱한 장치가 열리기 때문입니다.</summary>
        public string MidiInDeviceName { get; set; } = "";
        public string MidiOutDeviceName { get; set; } = "";

        /// <summary>표시 중이던 장치 탭(화면 전환 전용). 0=SPE3, 1=SPE5</summary>
        public int ActiveEngineIndex { get; set; } = 0;

        /// <summary>
        /// 배정하지 않은 채널을 처리할 기본 엔진. 0=SPE3, 1=SPE5.
        /// 화면 탭과는 별개입니다 - 탭을 바꿨다고 소리 경로가 바뀌면 안 되기 때문입니다.
        /// </summary>
        public int DefaultEngineIndex { get; set; } = 1;

        /// <summary>채널(0-15) → 엔진 인덱스(0=SPE3, 1=SPE5).</summary>
        public Dictionary<string, int> ChannelAssignments { get; set; } = new();

        // ---- 오디오 ----
        /// <summary>0.0 ~ 1.0</summary>
        public double MasterVolume { get; set; } = 0.8;
        public double Mt32Volume { get; set; } = 1.0;

        /// <summary>SPE3 마스터 볼륨(패널 노브, 1~100). 다음 실행 때 SysEx로 되살린다.</summary>
        public int Mt32MasterVolume { get; set; } = 100;
        public double Sc55Volume { get; set; } = 1.0;

        /// <summary>마지막으로 쓴 창 폭. 0이면 기본 폭. 사용자가 늘린 폭을 다음 실행 때도 그대로 쓴다.</summary>
        public double WindowWidth { get; set; } = 0;

        /// <summary>도움말 섹션을 펼쳐 뒀는지. 처음에는 접혀 있다.</summary>
        public bool HelpExpanded { get; set; } = false;

        /// <summary>마지막으로 있던 창 위치(왼쪽/위, 화면 좌표). null이면 저장된 위치 없음(윈도우 기본 위치).</summary>
        public double? WindowLeft { get; set; }
        public double? WindowTop { get; set; }

        /// <summary>사용자가 직접 조절한 창 높이. 0이면 내용에 맞춰 자동(설정을 접으면 줄어드는 기본 동작).</summary>
        public double WindowHeight { get; set; } = 0;

        /// <summary>SPE5 LCD 대비(1~16). 0이면 저장된 값 없음(펌웨어 기본값 사용). 종료할 때 저장하고 시작할 때 되돌린다.</summary>
        public int Sc55LcdContrast { get; set; } = 0;

        /// <summary>MIDI 종류. 0=직접 설정, 1=SPE3, 2=GM/GS(SPE5). 0이 아니면 시작할 때 그 종류를 적용한다.</summary>
        public int MidiKindIndex { get; set; } = 0;

        /// <summary>들어오는 MIDI(GM On, GS Reset, SPE3 SysEx)로 종류를 자동 감지해서 배정을 바꿀지.</summary>
        public bool AutoDetectKind { get; set; } = true;

        /// <summary>접어 둔 설정 섹션의 키(rom, map, channel, midi, sc55rom, audio, log). 다음 실행 때도 접힌 채로 둡니다.</summary>
        public List<string> CollapsedSections { get; set; } = new();

        /// <summary>화면 언어. "auto"(윈도 언어를 따름) / "ko" / "en". 바꾸면 다음 실행부터 적용.</summary>
        public string Language { get; set; } = "auto";

        /// <summary>시작할 때 자동으로 연결/재생을 시작할지.</summary>
        public bool AutoStart { get; set; } = false;

        // ------------------------------------------------------------------

        // 이 폴더 이름을 원본(SPWare.VirtualSoundCanvas)과 다르게 둔다 - 같은 이름을 쓰면 같은
        // %AppData% 설정 파일을 공유하게 되어, 이 SPE3/SPE5 전용판이 저장할 때마다 원본판의
        // SC-88Pro 관련 설정(Sc88RomFolder 등, 이 클래스엔 없는 필드)이 JSON 직렬화 과정에서
        // 통째로 사라져 버린다(두 프로그램을 같은 PC에서 같이 쓸 수 있어야 하므로 분리 필수).
        [JsonIgnore]
        public static string SettingsDirectory =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SPWare", "WindSound");

        [JsonIgnore]
        public static string SettingsPath => Path.Combine(SettingsDirectory, "settings.json");

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
        };

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return CreateWithDefaults();
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded == null) return CreateWithDefaults();
                loaded.ResolveRomPaths();
                return loaded;
            }
            catch
            {
                // 설정 파일이 깨졌더라도 앱은 떠야 합니다.
                return CreateWithDefaults();
            }
        }

        /// <summary>
        /// 저장에 실패해도 예외를 던지지 않습니다(설정 저장 실패로 앱이 죽으면 안 되므로).
        /// 성공 여부를 알고 싶으면 반환값을 확인하세요.
        /// </summary>
        public bool Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDirectory);
                // ROM 경로는 exe 폴더 기준 상대 경로로 저장한다(폴더째 옮겨도 그대로 동작). 메모리의 값은 그대로 둔다.
                var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
                copy.Mt32OldRomFolder = ToRelative(Mt32OldRomFolder);
                copy.Mt32NewRomFolder = ToRelative(Mt32NewRomFolder);
                copy.Cm32LRomFolder = ToRelative(Cm32LRomFolder);
                copy.Sc55RomFolder = ToRelative(Sc55RomFolder);
                File.WriteAllText(SettingsPath, JsonSerializer.Serialize(copy, JsonOptions));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string BaseDir =>
            Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory);

        /// <summary>exe와 같은 드라이브에 있으면 exe 폴더 기준 상대 경로로(예: Roms\SC55mk2), 아니면 그대로.</summary>
        private static string ToRelative(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) return path;
            try
            {
                string full = Path.GetFullPath(path);
                if (!string.Equals(Path.GetPathRoot(full), Path.GetPathRoot(BaseDir), StringComparison.OrdinalIgnoreCase))
                    return path; // 다른 드라이브는 상대 경로로 만들 수 없다
                return Path.GetRelativePath(BaseDir, full);
            }
            catch { return path; }
        }

        /// <summary>
        /// 저장된 ROM 경로를 쓸 수 있는 절대 경로로 바꾼다: 상대 경로는 exe 폴더 기준으로 풀고,
        /// 그 폴더가 없으면(예전에 저장된 경로가 사라졌거나 폴더를 옮긴 경우) exe 옆의 Roms\기본이름을 대신 쓴다.
        /// </summary>
        private void ResolveRomPaths()
        {
            string Fix(string saved, string sub)
            {
                string path = saved;
                if (!string.IsNullOrWhiteSpace(path) && !Path.IsPathRooted(path))
                    path = Path.GetFullPath(Path.Combine(BaseDir, path));
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path)) return path;
                string probe = Path.Combine(BaseDir, "Roms", sub);
                return Directory.Exists(probe) ? probe : path ?? "";
            }
            Mt32OldRomFolder = Fix(Mt32OldRomFolder, "MT32-old");
            Mt32NewRomFolder = Fix(Mt32NewRomFolder, "MT32-new");
            Cm32LRomFolder = Fix(Cm32LRomFolder, "CM32L");
            Sc55RomFolder = Fix(Sc55RomFolder, "SC55mk2");
        }

        /// <summary>
        /// 설정 파일이 아직 없을 때, exe 옆의 Roms 폴더가 있으면 그걸 기본값으로 씁니다.
        /// (기존처럼 프로젝트에 ROM을 같이 둔 사용자도 그대로 동작하도록)
        /// </summary>
        private static AppSettings CreateWithDefaults()
        {
            var s = new AppSettings();
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            string Probe(string sub)
            {
                string p = Path.Combine(baseDir, "Roms", sub);
                return Directory.Exists(p) ? p : "";
            }

            s.Mt32OldRomFolder = Probe("MT32-old");
            s.Mt32NewRomFolder = Probe("MT32-new");
            s.Cm32LRomFolder = Probe("CM32L");
            s.Sc55RomFolder = Probe("SC55mk2");
            return s;
        }

        /// <summary>선택된 SPE3 롬 세트의 폴더 경로.</summary>
        public string GetMt32Folder() => Mt32RomSetIndex switch
        {
            1 => Mt32NewRomFolder,
            2 => Cm32LRomFolder,
            _ => Mt32OldRomFolder,
        };

        public int GetChannelAssignment(int channel) =>
            ChannelAssignments.TryGetValue(channel.ToString(), out var v) ? v : -1;

        public void SetChannelAssignment(int channel, int engineIndex) =>
            ChannelAssignments[channel.ToString()] = engineIndex;
    }
}
