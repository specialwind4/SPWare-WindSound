using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SPWare.VirtualSoundCanvas.Engines;
using SPWare.VirtualSoundCanvas.Midi;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// SPWare MTS5 실기 디스플레이.
    ///
    /// 실기 동작을 따른 점:
    ///  - 윗줄에 파트 번호 + 악기 번호 + 악기 이름을 표시합니다(매뉴얼의 전원 투입 화면: 01 001 Piano 1)
    ///  - LEVEL / PAN / REVERB / CHORUS / K SHIFT / MIDI CH 여섯 값을 항상 표시
    ///  - 표시 대상 파트는 "마지막으로 소리가 난 파트"를 자동으로 따라갑니다
    ///    (실기에서 파트 버튼을 눌러 옮겨 다니는 것에 해당)
    ///  - 우측 16채널 바 인디케이터 (피크 홀드 포함)
    /// </summary>
    public partial class Sc55Display : UserControl
    {
        private MidiRouter? _router;
        private int _shownChannel;

        // ---- 전면 버튼 ----
        // 화면의 버튼을 누르는 동안 에뮬레이터의 실제 버튼 비트를 켠다(펌웨어가 실기처럼 반응).
        // 오른쪽 클릭은 '눌린 채로 고정'(다시 오른쪽 클릭하면 해제): 두 버튼을 동시에 눌러야 하는 조작을 마우스로 하기 위한 것.
        private readonly HashSet<int> _held = new();     // 마우스로 누르고 있는 버튼
        private readonly HashSet<int> _latched = new();  // 오른쪽 클릭으로 고정한 버튼
        private readonly Dictionary<int, FrameworkElement> _buttonViews = new();

        /// <summary>화면 버튼 -> 에뮬레이터 MCU_BUTTON_* 번호 (mcu.h). 모르는 버튼이면 false.</summary>
        private static bool TryButtonId(FrameworkElement el, out int id)
        {
            id = -1;
            if (el.Tag is "POWER") { id = 0; return true; } // MCU_BUTTON_POWER
            if (el.Tag is "ALL") { id = 6; return true; }   // MCU_BUTTON_INST_ALL
            if (el.Tag is "MUTE") { id = 5; return true; }  // MCU_BUTTON_INST_MUTE
            if (el.DataContext is not string label || el.Tag is not string side) return false;
            bool right = side == "R";
            (int l, int r)? pair = label switch
            {
                "PART" => (22, 14),
                "INSTRUMENT" => (3, 4),
                "LEVEL" => (20, 21),
                "PAN" => (12, 13),
                "REVERB" => (18, 19),
                "CHORUS" => (10, 11),
                "KEY SHIFT" => (16, 17),
                "MIDI CH" => (8, 9),
                _ => null,
            };
            if (pair is null) return false;
            id = right ? pair.Value.r : pair.Value.l;
            return true;
        }

        /// <summary>화살표 버튼이면 그 쌍의 (왼쪽, 오른쪽) 번호를 돌려준다. ALL/MUTE는 쌍이 없다.</summary>
        private static bool TryPairIds(FrameworkElement el, out int left, out int right)
        {
            left = right = -1;
            if (el.DataContext is not string label) return false;
            (int l, int r)? pair = label switch
            {
                "PART" => (22, 14),
                "INSTRUMENT" => (3, 4),
                "LEVEL" => (20, 21),
                "PAN" => (12, 13),
                "REVERB" => (18, 19),
                "CHORUS" => (10, 11),
                "KEY SHIFT" => (16, 17),
                "MIDI CH" => (8, 9),
                _ => null,
            };
            if (pair is null) return false;
            (left, right) = pair.Value;
            return true;
        }

        // Shift+클릭으로 누른 쌍(◀▶ 동시 누름). 뗄 때 두 버튼을 함께 뗀다.
        private readonly Dictionary<FrameworkElement, (int L, int R)> _chords = new();

        private static bool ChordRequested() =>
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        private void SetPairVisual(FrameworkElement el, int l, int r, bool down)
        {
            if (el.Parent is Grid g)
                foreach (var child in g.Children)
                    if (child is Border b)
                        b.Background = down ? new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x74)) : new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
        }

        private void ApplyButton(int id)
        {
            bool down = _held.Contains(id) || _latched.Contains(id);
            _lcdSource?.SetButton(id, down);
            if (_buttonViews.TryGetValue(id, out var view))
            {
                if (view is Border b) b.Background = down ? new SolidColorBrush(Color.FromRgb(0x6A, 0x6C, 0x74)) : new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10));
                else view.Opacity = down ? 0.55 : 1.0;
            }
        }

        private void OnPanelButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || !TryButtonId(el, out int id)) return;

            // Shift+클릭: 이 쌍의 ◀ ▶ 를 동시에 누른다 (매뉴얼: "PART [◀]와 [▶]를 동시에 누른다")
            if (ChordRequested() && TryPairIds(el, out int cl, out int cr))
            {
                _chords[el] = (cl, cr);
                _lcdSource?.SetButtons((1u << cl) | (1u << cr), (1u << cl) | (1u << cr));
                SetPairVisual(el, cl, cr, true);
                el.CaptureMouse();
                e.Handled = true;
                return;
            }

            _buttonViews[id] = el;
            _held.Add(id);
            el.CaptureMouse(); // 포인터가 버튼 밖으로 나가도 뗄 때 이벤트를 받는다
            ApplyButton(id);
            e.Handled = true;
        }

        private void OnPanelButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || !TryButtonId(el, out int id)) return;
            if (_chords.Remove(el, out var chord))
            {
                _lcdSource?.SetButtons((1u << chord.L) | (1u << chord.R), 0);
                SetPairVisual(el, chord.L, chord.R, false);
                el.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }
            _held.Remove(id);
            el.ReleaseMouseCapture();
            ApplyButton(id);
            e.Handled = true;
        }

        private void OnPanelButtonLost(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not FrameworkElement el || !TryButtonId(el, out int id)) return;
            if (_chords.Remove(el, out var chord))
            {
                _lcdSource?.SetButtons((1u << chord.L) | (1u << chord.R), 0);
                SetPairVisual(el, chord.L, chord.R, false);
                return;
            }
            if (_held.Remove(id)) ApplyButton(id);
        }

        private void OnPanelButtonLatch(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is not FrameworkElement el || !TryButtonId(el, out int id)) return;
            _buttonViews[id] = el;
            if (!_latched.Remove(id)) _latched.Add(id);
            ApplyButton(id);
            e.Handled = true;
        }

        // ---- VOLUME 노브 ----
        // 실기의 볼륨 노브 = 이 앱에서는 MTS5의 음량(슬라이더와 같은 값), 범위는 1~100.
        // 마우스로 노브를 잡고 손으로 돌리듯 원을 그리며 돌린다: 포인터가 노브 중심을 기준으로 움직인 각도만큼 노브가 돈다.
        // 노브는 -135도(1) ~ +135도(100)를 돌고, 아래쪽 90도는 못 돈다(실기 노브의 멈춤 위치). 마우스 휠은 한 칸에 2씩.
        private const int VolumeMin = 1, VolumeMax = 100;
        private const double KnobMinAngle = -135, KnobMaxAngle = 135;
        private int _volumeValue = VolumeMax;
        private double _knobAngle = KnobMaxAngle;   // 드래그 중의 연속 각도
        private double _lastPointerAngle;
        private bool _knobDragging;
        private readonly System.Windows.Controls.ToolTip _knobTip = new()
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Right,
            StaysOpen = true,
        };
        private readonly DispatcherTimer _knobTipTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

        /// <summary>노브를 사용자가 돌렸을 때(1~100 정수를 0.01~1.0으로). 슬라이더 쪽 값은 MainWindow가 맞춘다.</summary>
        public event Action<double>? VolumeKnobChanged;

        private static double AngleOf(int value) =>
            KnobMinAngle + (value - VolumeMin) / (double)(VolumeMax - VolumeMin) * (KnobMaxAngle - KnobMinAngle);

        private static int ValueOf(double angle) =>
            (int)Math.Round(VolumeMin + (angle - KnobMinAngle) / (KnobMaxAngle - KnobMinAngle) * (VolumeMax - VolumeMin));

        /// <summary>바깥(슬라이더)에서 값이 바뀌었을 때 노브 모양만 맞춘다(이벤트는 올리지 않음). 드래그 중에는 건드리지 않는다.</summary>
        public void SetVolumeKnob(double value01)
        {
            _volumeValue = Math.Clamp((int)Math.Round(value01 * 100), VolumeMin, VolumeMax);
            if (_knobDragging) return;
            _knobAngle = AngleOf(_volumeValue);
            VolumeRotate.Angle = _knobAngle;
        }

        private void ShowKnobTip()
        {
            _knobTip.Content = $"VOLUME {_volumeValue}";
            _knobTip.PlacementTarget = VolumeKnob;
            VolumeKnob.ToolTip = _knobTip;
            _knobTip.IsOpen = true;
            _knobTipTimer.Stop();
            if (!_knobDragging) _knobTipTimer.Start();
        }

        private void RaiseVolume(int value)
        {
            value = Math.Clamp(value, VolumeMin, VolumeMax);
            if (value == _volumeValue) return;
            _volumeValue = value;
            VolumeKnobChanged?.Invoke(value / 100.0);
        }

        /// <summary>노브 중심을 기준으로 포인터가 가리키는 방향(위 = 0도, 시계 방향이 +).</summary>
        private double PointerAngle(System.Windows.Input.MouseEventArgs e)
        {
            var center = VolumeKnob.TranslatePoint(new Point(VolumeKnob.ActualWidth / 2, VolumeKnob.ActualHeight / 2), this);
            var p = e.GetPosition(this);
            return Math.Atan2(p.X - center.X, -(p.Y - center.Y)) * 180.0 / Math.PI;
        }

        private void OnKnobDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _knobDragging = true;
            _knobAngle = AngleOf(_volumeValue);
            _lastPointerAngle = PointerAngle(e);
            VolumeKnob.CaptureMouse();
            ShowKnobTip();
            e.Handled = true;
        }

        private void OnKnobMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (!VolumeKnob.IsMouseCaptured) return;
            double now = PointerAngle(e);
            double delta = now - _lastPointerAngle;
            if (delta > 180) delta -= 360;        // 위쪽(±180도 경계)을 넘을 때 값이 튀지 않게
            else if (delta < -180) delta += 360;
            _lastPointerAngle = now;

            _knobAngle = Math.Clamp(_knobAngle + delta, KnobMinAngle, KnobMaxAngle);
            VolumeRotate.Angle = _knobAngle;
            RaiseVolume(ValueOf(_knobAngle));
            ShowKnobTip();
        }

        private void OnKnobUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            _knobDragging = false;
            VolumeKnob.ReleaseMouseCapture();
            _knobAngle = AngleOf(_volumeValue);   // 정수 값에 맞는 각도로 정리
            VolumeRotate.Angle = _knobAngle;
            ShowKnobTip();                        // 잠깐 더 보여 준 뒤 사라진다
            e.Handled = true;
        }

        private void OnKnobWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            RaiseVolume(_volumeValue + (e.Delta > 0 ? 2 : -2));
            _knobAngle = AngleOf(_volumeValue);
            VolumeRotate.Angle = _knobAngle;
            ShowKnobTip();
            e.Handled = true;
        }

        // ---- 진짜 LCD 화면 ----
        // 에뮬레이터가 그린 LCD 픽셀을 주기적으로 가져와서 그대로 보여준다. 이 패널이 보일 때만 돈다.
        private Sc55Engine? _lcdSource;
        private readonly uint[] _frame = new uint[1024 * 1024];
        private WriteableBitmap? _bitmap;
        private bool _live;
        private readonly DispatcherTimer _lcdTimer = new() { Interval = TimeSpan.FromMilliseconds(40) };

        private void PumpLcd()
        {
            if (_lcdSource is null || !_lcdSource.TryReadLcd(_frame, out int w, out int h)) return;

            if (_bitmap is null || _bitmap.PixelWidth != w || _bitmap.PixelHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                LcdImage.Source = _bitmap;
            }
            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), _frame, w * 4, 0);

            if (!_live)
            {
                _live = true;
                // 이미지 배경(첫 픽셀)과 같은 색으로 LCD 바탕을 맞추고 여백을 없앤다
                uint p = _frame[0];
                LcdSurface.Background = new SolidColorBrush(Color.FromRgb((byte)(p >> 16), (byte)(p >> 8), (byte)p));
                LcdSurface.Padding = new Thickness(0);
                FallbackLcd.Visibility = Visibility.Collapsed;
                LcdImage.Visibility = Visibility.Visible;
            }
        }

        public Sc55Display()
        {
            InitializeComponent();
            _lcdTimer.Tick += (_, _) => PumpLcd();
            _knobTipTimer.Tick += (_, _) => { _knobTipTimer.Stop(); _knobTip.IsOpen = false; };
            IsVisibleChanged += (_, _) =>
            {
                if (IsVisible && _lcdSource is not null) _lcdTimer.Start();
                else _lcdTimer.Stop();
            };
            // SC 계열 주황 LCD의 진한 갈색 도트
            Bars.ApplyPalette(System.Windows.Media.Color.FromRgb(0x24, 0x10, 0x00));
        }

        public void Attach(MidiRouter router, ISynthEngine engine)
        {
            _router = router;
            _lcdSource = engine as Sc55Engine;
            if (_lcdSource is not null && IsVisible) _lcdTimer.Start();

            // 소리가 난 파트로 표시를 따라가게 (실기에서 파트를 옮기는 것에 해당)
            router.ChannelActivity += (_, m) =>
            {
                if (m.engine != engine) return; // 이 패널의 엔진으로 가는 노트만 표시
                Bars.SetLevel(m.channel, m.level);
                if (m.channel != _shownChannel)
                {
                    _shownChannel = m.channel;
                    Refresh();
                }
            };
            router.ChannelStateChanged += (_, channel) =>
            {
                if (channel == _shownChannel) Refresh();
            };
            Refresh();
        }

        private void Refresh()
        {
            if (_router is null) return;
            var s = _router.GetChannelState(_shownChannel);

            bool isDrum = _shownChannel == 9; // 실기와 동일하게 채널 10은 리듬 파트
            string name = isDrum
                ? GmInstrumentNames.GetDrumKitName(s.Program)
                : GmInstrumentNames.GetMelodicName(s.Program);
            // 실기 LCD 윗줄(매뉴얼 전원 투입 화면 '01 001 Piano 1'): 파트 번호 2자리 + 악기 번호 3자리 + 악기 이름.
            BannerText.Text = $"{_shownChannel + 1:00} {s.Program + 1:000} {name}";

            LevelText.Text = s.Level.ToString();
            PanText.Text = s.Pan switch
            {
                64 => "0",
                < 64 => $"L{64 - s.Pan}",
                _ => $"R{s.Pan - 64}",
            };
            ReverbText.Text = s.Reverb.ToString();
            ChorusText.Text = s.Chorus.ToString();
            KeyShiftText.Text = "0"; // Key Shift는 실기 패널 조작값이라 MIDI로 관측되지 않음
            MidiChText.Text = $"{_shownChannel + 1:00}"; // MTS5는 A/B 그룹이 없어 01~16
        }
    }
}
