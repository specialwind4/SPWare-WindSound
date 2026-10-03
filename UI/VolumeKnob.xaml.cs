using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// 손으로 돌리듯 조작하는 볼륨 노브(1~100 정수).
    ///
    /// 노브를 잡고 원을 그리며 돌린다: 포인터가 노브 중심을 기준으로 움직인 각도만큼 노브가 돈다.
    /// -135도(1) ~ +135도(100)를 돌고, 아래쪽 90도는 못 돈다(실기 노브의 멈춤 위치). 마우스 휠은 한 칸에 2씩.
    /// 값이 바뀔 때 노브 옆에 "VOLUME nn" 표시가 뜬다.
    /// </summary>
    public partial class VolumeKnob : UserControl
    {
        private const int Min = 1, Max = 100;
        private const double MinAngle = -135, MaxAngle = 135;

        private int _value = Max;
        private double _angle = MaxAngle;      // 드래그 중의 연속 각도
        private double _lastPointerAngle;
        private bool _dragging;

        private readonly ToolTip _tip = new() { Placement = PlacementMode.Right, StaysOpen = true };
        private readonly DispatcherTimer _tipTimer = new() { Interval = TimeSpan.FromMilliseconds(900) };

        /// <summary>사용자가 노브를 돌려 값이 바뀌었을 때(1~100). <see cref="SetValue"/>로 바깥에서 바꾼 것은 알리지 않는다.</summary>
        public event Action<int>? ValueChanged;

        public int Value => _value;

        public VolumeKnob()
        {
            InitializeComponent();
            Root.MouseLeftButtonDown += OnDown;
            Root.MouseMove += OnMove;
            Root.MouseLeftButtonUp += OnUp;
            Root.MouseWheel += OnWheel;
            _tipTimer.Tick += (_, _) => { _tipTimer.Stop(); _tip.IsOpen = false; };
        }

        private static double AngleOf(int value) => MinAngle + (value - Min) / (double)(Max - Min) * (MaxAngle - MinAngle);

        private static int ValueOf(double angle) =>
            (int)Math.Round(Min + (angle - MinAngle) / (MaxAngle - MinAngle) * (Max - Min));

        /// <summary>바깥(슬라이더)에서 값이 바뀌었을 때 노브 모양만 맞춘다(이벤트 없음). 드래그 중에는 각도를 건드리지 않는다.</summary>
        public void SetValue(int value)
        {
            if (_dragging) return;
            _value = Math.Clamp(value, Min, Max);
            _angle = AngleOf(_value);
            Rotate.Angle = _angle;
        }

        private void ShowTip()
        {
            _tip.Content = $"VOLUME {_value}";
            _tip.PlacementTarget = Root;
            Root.ToolTip = _tip;
            _tip.IsOpen = true;
            _tipTimer.Stop();
            if (!_dragging) _tipTimer.Start();
        }

        private void Raise(int value)
        {
            value = Math.Clamp(value, Min, Max);
            if (value == _value) return;
            _value = value;
            ValueChanged?.Invoke(value);
        }

        /// <summary>노브 중심을 기준으로 포인터가 가리키는 방향(위 = 0도, 시계 방향이 +).</summary>
        private double PointerAngle(MouseEventArgs e)
        {
            var p = e.GetPosition(Root);
            return Math.Atan2(p.X - Root.ActualWidth / 2, -(p.Y - Root.ActualHeight / 2)) * 180.0 / Math.PI;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _dragging = true;
            _angle = AngleOf(_value);
            _lastPointerAngle = PointerAngle(e);
            Root.CaptureMouse();
            ShowTip();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!Root.IsMouseCaptured) return;
            double now = PointerAngle(e);
            double delta = now - _lastPointerAngle;
            if (delta > 180) delta -= 360;        // 위쪽(±180도 경계)을 넘을 때 값이 튀지 않게
            else if (delta < -180) delta += 360;
            _lastPointerAngle = now;

            _angle = Math.Clamp(_angle + delta, MinAngle, MaxAngle);
            Rotate.Angle = _angle;
            Raise(ValueOf(_angle));
            ShowTip();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
            _angle = AngleOf(_value);             // 정수 값에 맞는 각도로 정리
            Rotate.Angle = _angle;
            ShowTip();                            // 잠깐 더 보여 준 뒤 사라진다
            e.Handled = true;
        }

        private void OnWheel(object sender, MouseWheelEventArgs e)
        {
            Raise(_value + (e.Delta > 0 ? 2 : -2));
            _angle = AngleOf(_value);
            Rotate.Angle = _angle;
            ShowTip();
            e.Handled = true;
        }
    }
}
