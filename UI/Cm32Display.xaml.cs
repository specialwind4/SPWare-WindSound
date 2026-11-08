using System;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SPWare.VirtualSoundCanvas.Engines;
using SPWare.VirtualSoundCanvas.Midi;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// SPE3L 앞판. 액정도 버튼도 없고, VOLUME 노브 하나와 램프 둘(MIDI MESSAGE, POWER), 전원 스위치뿐이다.
    /// SPE3L은 SPE3와 같은 엔진(mt32emu)에 다른 ROM 세트를 올린 것이라, 이 패널의 노브는
    /// SPE3 패널의 SELECT/VOLUME 노브와 같은 마스터 볼륨(SysEx)을 돌린다(MainWindow가 두 노브를 맞춰 준다).
    /// </summary>
    public partial class Cm32Display : UserControl
    {
        public Cm32Display()
        {
            InitializeComponent();

            // 노브: 회색 플라스틱 원통, 윗면에 밝은 표시선 하나
            var face = new RadialGradientBrush { GradientOrigin = new System.Windows.Point(0.38, 0.3), Center = new System.Windows.Point(0.45, 0.4), RadiusX = 0.75, RadiusY = 0.75 };
            face.GradientStops.Add(new GradientStop(Color.FromRgb(0x86, 0x89, 0x8D), 0));
            face.GradientStops.Add(new GradientStop(Color.FromRgb(0x70, 0x73, 0x77), 0.55));
            face.GradientStops.Add(new GradientStop(Color.FromRgb(0x5E, 0x61, 0x65), 1));
            Knob.SetLook(face, new SolidColorBrush(Color.FromRgb(0x44, 0x47, 0x4B)),
                         new SolidColorBrush(Color.FromRgb(0xB6, 0xB8, 0xBB)), 2.4, 12);

            Knob.ValueChanged += v =>
            {
                _lastKnobTick = Environment.TickCount64;
                MasterVolumeChanged?.Invoke(v);
            };
            SwitchHit.MouseLeftButtonUp += (_, _) => PowerToggleRequested?.Invoke(!_powered);
            ApplyLeds();
        }

        /// <summary>전원 스위치를 눌렀을 때. 인수는 바라는 상태(true = 켜기). 실제로 끄고 켜는 일은 MainWindow가 한다.</summary>
        public event Action<bool>? PowerToggleRequested;

        // ---- VOLUME 노브 = 마스터 볼륨 ----
        /// <summary>노브를 사용자가 돌렸을 때(1~100). MainWindow가 엔진에 마스터 볼륨 SysEx를 보내고 SPE3 패널 노브도 맞춘다.</summary>
        public event Action<int>? MasterVolumeChanged;

        /// <summary>현재 노브 값(1~100).</summary>
        public int MasterVolume => Knob.Value;

        /// <summary>바깥(설정 복원, SPE3 패널 노브 등)에서 노브 모양만 맞춘다(이벤트 없음).</summary>
        public void SetMasterVolume(int value)
        {
            Knob.SetValue(Math.Clamp(value, 1, 100));
            // 방금 우리가 보낸 값이 엔진에 반영되어 액정 문구에 나올 때까지(엔진이 소리를 만들기 시작해야 SysEx를 처리한다) 옛 값을 따라가지 않는다
            _lastKnobTick = Environment.TickCount64 + 1500;
        }

        private long _lastKnobTick;

        // 게임이 SysEx로 마스터 볼륨을 바꾸면 엔진 액정 문구의 "Vol:nn"에 나온다(SPE3 패널과 같은 방식). 그 위치로 노브를 돌린다.
        private static readonly System.Text.RegularExpressions.Regex VolRegex =
            new(@"[Vv]ol[:>]\s*(\d{1,3})", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>엔진(Mt32Engine) 액정 문구가 바뀔 때마다 부른다. 액정이 없어서 화면은 없고, 볼륨 값만 따라간다.</summary>
        public void OnEngineLcdTextChanged(object? sender, string text)
        {
            // 사용자가 방금 돌렸으면 잠깐은 액정 값(아직 옛 값일 수 있음)을 따르지 않는다
            if (Environment.TickCount64 - _lastKnobTick < 500) return;
            var m = VolRegex.Match(text ?? "");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v) && v >= 1 && v <= 100)
                Knob.SetValue(v);
        }

        // ---- 램프 ----
        // MIDI MESSAGE: MIDI를 받을 때마다 깜박인다. MIDI 스레드에서 UI를 건드리지 않도록 받은 시각만 적고,
        // UI 타이머가 그 시각을 보고 켜고 끈다(SPE3 패널과 같은 방식).
        // POWER: 엔진이 열려 있으면(소리가 날 수 있는 상태) 켜져 있다.
        private static readonly Brush MidiOn = new SolidColorBrush(Color.FromRgb(0x4C, 0xE0, 0x10));
        private static readonly Brush MidiOff = new SolidColorBrush(Color.FromRgb(0x93, 0xAE, 0x63));
        private static readonly Brush PowerOnBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x1A, 0x5E));
        private static readonly Brush PowerOffBrush = new SolidColorBrush(Color.FromRgb(0xB9, 0x70, 0x7A));
        private const long LedHoldMs = 90;
        private long _lastActivityTick;
        private bool _midiLit;
        private bool _powered;
        private readonly DispatcherTimer _ledTimer = new() { Interval = TimeSpan.FromMilliseconds(30) };

        /// <summary>POWER 램프(엔진이 열려 있는지). 바뀔 때만 다시 그린다.</summary>
        public void SetPowered(bool on)
        {
            if (_powered == on) return;
            _powered = on;
            ApplyLeds();
        }

        private void ApplyLeds()
        {
            bool midiLit = _midiLit && _powered;   // 꺼져 있으면 MIDI가 와도 램프는 꺼진 채
            MidiLed.Fill = midiLit ? MidiOn : MidiOff;
            MidiLed.Effect = midiLit
                ? new DropShadowEffect { Color = Color.FromRgb(0x6C, 0xFF, 0x2A), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.9 }
                : null;
            // 스위치: 켜짐 = 막대가 오른쪽(홈이 왼쪽), 꺼짐 = 막대가 왼쪽(홈이 오른쪽)
            Canvas.SetLeft(SwitchBar, _powered ? 566 : 558);
            Canvas.SetLeft(SwitchHollow, _powered ? 558 : 597);
            PowerLed.Fill = _powered ? PowerOnBrush : PowerOffBrush;
            PowerLed.Effect = _powered
                ? new DropShadowEffect { Color = Color.FromRgb(0xFF, 0x40, 0x80), BlurRadius = 9, ShadowDepth = 0, Opacity = 0.9 }
                : null;
        }

        /// <summary>라우터를 연결해서, 이 패널의 엔진으로 가는 노트에 MIDI MESSAGE 램프가 반응하게 합니다.</summary>
        public void Attach(MidiRouter router, ISynthEngine engine)
        {
            router.ChannelActivity += (_, m) =>
            {
                if (m.engine == engine) _lastActivityTick = Environment.TickCount64;
            };
            _ledTimer.Tick += (_, _) =>
            {
                bool lit = Environment.TickCount64 - _lastActivityTick < LedHoldMs;
                if (lit == _midiLit) return;
                _midiLit = lit;
                ApplyLeds();
            };
            _ledTimer.Start();
        }
    }
}
