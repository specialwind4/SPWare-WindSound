using System;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SPWare.VirtualSoundCanvas.Engines;
using SPWare.VirtualSoundCanvas.Midi;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// SPWare SPE3 실기 디스플레이. 실기는 20자 × 1줄 LCD 하나뿐입니다.
    ///
    /// mt32emu 본체(mt32_wrap.dll)의 getDisplayState()가 실제 LCD 텍스트를 그대로
    /// 돌려줍니다 - mt32-pi(라즈베리파이용 실물 SPE3 에뮬레이터)가 물리 LCD에
    /// 표시하는 것과 같은 값입니다. 100ms마다 폴링해서 바뀔 때만 갱신하므로,
    /// 여기서는 그냥 받은 텍스트를 그대로 보여주면 됩니다(SPE5/88처럼 라우터를
    /// 감시해서 근사치를 만들 필요가 없어졌습니다).
    /// </summary>
    public partial class Mt32Display : UserControl
    {
        private const int Columns = 20; // SPE3 LCD 가로 글자 수

        public Mt32Display()
        {
            InitializeComponent();
            Write("SPWare  SPE3");
            Knob.ValueChanged += v =>
            {
                _lastKnobTick = Environment.TickCount64;
                if (_volumeArmed && _selectedPart != 0)
                {
                    _partVolumes[_selectedPart] = v;
                    PartVolumeChanged?.Invoke(_selectedPart, v);
                    CustomMessageRequested?.Invoke($"PART {PartLabel(_selectedPart)}  VOL: {v,3}");
                    UpdateButtonHighlights();
                }
                else
                {
                    _masterVolumeValue = v;
                    MasterVolumeChanged?.Invoke(v);
                    // 액정을 한 번이라도 우리 문구로 덮은 뒤로는(PART/VOLUME 사용) 엔진이 다시는 "기본 화면"으로
                    // 스스로 돌아가지 않으므로(아래 MainScreenText 주석 참고), 마스터 볼륨이 바뀔 때마다 우리가
                    // 최신 값으로 다시 그려준다. 한 번도 안 썼다면 엔진의 진짜 화면을 그대로 둔다(기존 동작 그대로).
                    if (_tookOverDisplay) CustomMessageRequested?.Invoke(MainScreenText(v));
                }
            };
            UpdateButtonHighlights();
        }

        // ---- PART 선택 + VOLUME(그 파트 음량) ----
        // 실기: PART 버튼으로 파트를 고르고, VOLUME 버튼을 누른 뒤 SELECT/VOLUME 노브로 그 파트의 음량(0~100)을 조절한다.
        // Munt(엔진 본체)는 버튼 반응 자체는 구현하지 않으므로(공식 주석: 앞판 버튼은 범위 밖) 여기서 상태를 관리하고,
        // 실제 값은 Mt32Engine.SetPartVolume이 매뉴얼에 나온 SysEx 주소로 보낸다. 액정 안내 문구도 우리가 직접 만들어 보낸다.
        private static readonly Brush BtnOff = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
        private static readonly Brush BtnOffBorder = new SolidColorBrush(Color.FromRgb(0x4A, 0x4B, 0x51));
        private static readonly Brush BtnOn = new SolidColorBrush(Color.FromRgb(0x6C, 0x63, 0xFF));
        private static readonly Brush BtnOnBorder = new SolidColorBrush(Color.FromRgb(0x9C, 0x96, 0xFF));

        private readonly int[] _partVolumes = { 0, 100, 100, 100, 100, 100, 100, 100, 100, 100 }; // 인덱스 1~9(9=리듬), 0은 미사용
        private int _selectedPart;      // 0 = 선택 없음, 1~8 = 파트, 9 = 리듬
        private bool _volumeArmed;      // true면 노브가 지금 _selectedPart의 음량을 조절 중
        private int _masterVolumeValue = 100; // 노브가 음량 모드로 갔다가 돌아올 때 되살릴 마스터 볼륨 값
        private bool _tookOverDisplay;  // PART/VOLUME을 한 번이라도 쓰면 true(아래 MainScreenText 주석 참고)

        /// <summary>사용자가 파트 음량을 바꿨을 때(1~8, 9=리듬 / 0~100). MainWindow가 Mt32Engine.SetPartVolume을 부른다.</summary>
        public event Action<int, int>? PartVolumeChanged;

        /// <summary>액정에 우리 문구를 띄워 달라는 요청(20자 이내 권장). MainWindow가 Mt32Engine.ShowCustomMessage를 부른다.</summary>
        public event Action<string>? CustomMessageRequested;

        // 노브가 음량 모드에서 풀려날 때 액정에 실기의 기본 화면(파트 상태 + 마스터 볼륨)과 같은 모양을 직접 만들어 보여준다.
        // Munt의 진짜 "기본 화면 복귀"(Display Reset, 주소 20 01 00)를 실제로 보내 봤는데 구형/신형 ROM 둘 다
        // 제어문자가 섞여 화면이 깨졌다(원인: 우리 ROM들의 실제 컨트롤 ROM 버전이 이 기능을 지원하는 V2.04보다
        // 낮은 듯 - Munt 소스 주석에 "V2.04에서 Display Reset이 새로 생겼다"고 돼 있다). 그래서 대신 우리가 만든
        // 문구로 같은 모양을 흉내 낸다. 대가: 한 번이라도 이 문구를 보내면 엔진은 계속 "커스텀 메시지 화면"으로
        // 여기고(실기 사양상 커스텀 메시지가 최우선), 스스로는 기본 화면으로 못 돌아간다 - 그래서 마스터 볼륨이
        // 바뀔 때마다 이 문구를 다시 그려서 최신 값을 유지한다(위 Knob.ValueChanged 참고).
        private static string MainScreenText(int masterVolume) => $"1 2 3 4 5 R |vol:{masterVolume,3}";

        private Border PartBorder(int part) => part switch
        {
            1 => PartBtn1, 2 => PartBtn2, 3 => PartBtn3, 4 => PartBtn4, 5 => PartBtn5,
            6 => PartBtn1, 7 => PartBtn2, 8 => PartBtn3, // 6/7/8은 1/2/3 버튼을 Shift+클릭
            9 => PartBtnRhythm,
            _ => null!,
        };

        private void UpdateButtonHighlights()
        {
            foreach (var b in new[] { PartBtn1, PartBtn2, PartBtn3, PartBtn4, PartBtn5, PartBtnRhythm })
            {
                b.Background = BtnOff;
                b.BorderBrush = BtnOffBorder;
                b.Effect = null;
            }
            if (_selectedPart != 0)
            {
                var b = PartBorder(_selectedPart);
                b.Background = BtnOn;
                b.BorderBrush = BtnOnBorder;
                b.Effect = new DropShadowEffect { Color = Color.FromRgb(0x6C, 0x63, 0xFF), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 };
            }

            bool armed = _volumeArmed && _selectedPart != 0;
            VolumeBtn.Background = armed ? BtnOn : BtnOff;
            VolumeBtn.BorderBrush = armed ? BtnOnBorder : BtnOffBorder;
            VolumeBtn.Effect = armed
                ? new DropShadowEffect { Color = Color.FromRgb(0x6C, 0x63, 0xFF), BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 }
                : null;
        }

        private static string PartLabel(int part) => part == 9 ? "R" : part.ToString();

        private void OnPartButtonDown(object sender, MouseButtonEventArgs e)
        {
            var border = (Border)sender;
            int part = int.Parse((string)border.Tag);
            // Shift+클릭 = 실기의 "MASTER VOLUME을 누른 채 PART 1/2/3" 조합 -> 파트 6/7/8
            if (part is 1 or 2 or 3 && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) part += 5;

            _selectedPart = part;
            // 실기 절차: 파트를 새로 고르면 VOLUME은 다시 눌러야 한다 -> 음량 모드는 일단 풀어준다
            if (_volumeArmed)
            {
                _volumeArmed = false;
                Knob.SetValue(_masterVolumeValue);
                CustomMessageRequested?.Invoke(MainScreenText(_masterVolumeValue));
            }
            UpdateButtonHighlights();
            e.Handled = true;
        }

        private void OnVolumeButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_selectedPart == 0) { e.Handled = true; return; } // 파트를 먼저 골라야 한다

            _volumeArmed = !_volumeArmed;
            if (_volumeArmed)
            {
                _tookOverDisplay = true;
                Knob.SetValue(_partVolumes[_selectedPart]); // 노브를 그 파트의 지금 음량 위치로
                CustomMessageRequested?.Invoke($"PART {PartLabel(_selectedPart)}  VOL: {_partVolumes[_selectedPart],3}");
            }
            else
            {
                Knob.SetValue(_masterVolumeValue); // 노브를 마스터 볼륨 위치로 되돌린다
                CustomMessageRequested?.Invoke(MainScreenText(_masterVolumeValue));
            }
            UpdateButtonHighlights();
            e.Handled = true;
        }

        /// <summary>RESET 버튼. MainWindow가 SPE3에 리셋 SysEx를 보내고 마스터 볼륨을 다시 맞춘다.</summary>
        public event Action? ResetRequested;

        private void OnResetButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 리셋하면 파트 음량은 모두 100으로 돌아가므로 패널이 기억하는 값도 맞춘다
            for (int i = 1; i < _partVolumes.Length; i++) _partVolumes[i] = 100;
            _volumeArmed = false;
            Knob.SetValue(_masterVolumeValue);
            UpdateButtonHighlights();
            ResetRequested?.Invoke();
            e.Handled = true;
        }

        // ---- SELECT/VOLUME 노브 = SPE3 마스터 볼륨 ----
        /// <summary>노브를 사용자가 돌렸을 때(1~100). MainWindow가 SPE3에 마스터 볼륨 SysEx를 보낸다.</summary>
        public event Action<int>? MasterVolumeChanged;

        /// <summary>현재 노브 값(1~100).</summary>
        public int MasterVolume => Knob.Value;

        /// <summary>바깥(설정 복원 등)에서 노브 모양만 맞춘다(이벤트 없음).</summary>
        public void SetMasterVolume(int value)
        {
            _masterVolumeValue = Math.Clamp(value, 1, 100);
            if (!_volumeArmed) Knob.SetValue(_masterVolumeValue);
        }

        private long _lastKnobTick;

        // 액정의 "Vol:nn"(게임이 SysEx로 볼륨을 바꿔도 여기에 나온다)을 읽어 노브를 그 위치로 돌린다.
        private static readonly System.Text.RegularExpressions.Regex VolRegex =
            new(@"[Vv]ol[:>]\s*(\d{1,3})", System.Text.RegularExpressions.RegexOptions.Compiled);

        private void FollowLcdVolume(string text)
        {
            if (_volumeArmed) return; // 지금 노브는 파트 음량을 조절 중 - 마스터 볼륨 문구를 따라가지 않는다
            // 사용자가 방금 돌렸으면 잠깐은 액정 값(아직 옛 값일 수 있음)을 따르지 않는다
            if (Environment.TickCount64 - _lastKnobTick < 500) return;
            var m = VolRegex.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v >= 1 && v <= 100)
                Knob.SetValue(v);
        }

        // ---- MIDI MESSAGE 램프 ----
        // 실기는 MIDI를 받을 때마다 램프가 깜박인다. 여기서는 SPE3로 가는 노트가 들어온 시각만 기록하고
        // (MIDI 스레드에서 UI를 직접 건드리지 않도록), UI 타이머가 그 시각을 보고 램프를 켜고 끈다.
        private static readonly Brush LedOn = new SolidColorBrush(Color.FromRgb(0x3C, 0xF0, 0x3C));
        private static readonly Brush LedOff = new SolidColorBrush(Color.FromRgb(0x12, 0x3B, 0x12));
        private const long LedHoldMs = 90;
        private long _lastActivityTick;
        private bool _ledLit;
        private readonly DispatcherTimer _ledTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

        /// <summary>라우터를 연결해서, 이 패널의 엔진(SPE3)으로 가는 노트에 램프가 반응하게 합니다.</summary>
        public void Attach(MidiRouter router, ISynthEngine engine)
        {
            router.ChannelActivity += (_, m) =>
            {
                if (m.engine == engine) _lastActivityTick = Environment.TickCount64;
            };
            _ledTimer.Tick += (_, _) =>
            {
                bool lit = Environment.TickCount64 - _lastActivityTick < LedHoldMs;
                if (lit == _ledLit) return;
                _ledLit = lit;
                MidiLed.Background = lit ? LedOn : LedOff;
                MidiLed.Effect = lit
                    ? new DropShadowEffect { Color = Color.FromRgb(0x3C, 0xF0, 0x3C), BlurRadius = 10, ShadowDepth = 0, Opacity = 0.9 }
                    : null;
            };
            _ledTimer.Start();
        }

        /// <summary>Mt32Engine.LcdTextChanged에 그대로 연결하는 핸들러.</summary>
        public void OnEngineLcdTextChanged(object? sender, string text)
        {
            Write(text);
            FollowLcdVolume(text);
        }

        /// <summary>항상 20자 폭을 유지하도록 자르거나 공백으로 채워서 출력.</summary>
        private void Write(string text)
        {
            text ??= string.Empty;
            // 배포판 이름 표기: 엔진(ROM 펌웨어)이 보내는 글자의 제품명도 같은 이름으로 바꿔 보여 준다(글자 수는 유지).
            text = text.Replace("Roland", "SPWare").Replace("MT-32", "SPE3 ").Replace("CM-32L", "SPE3L ");
            if (text.Length > Columns) text = text.Substring(0, Columns);
            LcdText.Text = text.PadRight(Columns);
        }
    }
}
