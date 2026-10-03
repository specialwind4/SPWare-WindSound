using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// MTS5/SC-88 실기 전면 LCD의 16채널 바 인디케이터를 재현한 공용 컨트롤.
    ///
    /// 실기 동작을 따른 점:
    ///  - 아래에서 위로 차오르는 도트 매트릭스 막대
    ///  - "Peak Hold" (실기 시스템 파라미터에도 있는 기능): 최고점 도트를 잠시 붙잡아 둔 뒤
    ///    서서히 아래로 떨어뜨립니다
    ///  - 맨 아랫줄 도트는 실기와 동일하게 "그 파트가 살아있음(뮤트 아님)"을 뜻하는
    ///    상시 점등 도트로 씁니다
    /// </summary>
    public partial class ChannelBarGraph : UserControl
    {
        private const int ChannelCount = 16;
        private const int Segments = 10;

        private const double DecayPerTick = 0.055;   // 막대가 내려가는 속도
        private const int PeakHoldTicks = 18;        // 최고점을 붙잡아두는 시간(틱)
        private const double PeakFallPerTick = 0.022; // 붙잡은 뒤 최고점이 떨어지는 속도

        private readonly double[] _levels = new double[ChannelCount];
        private readonly double[] _peaks = new double[ChannelCount];
        private readonly int[] _peakHold = new int[ChannelCount];
        private readonly Rectangle[,] _cells = new Rectangle[ChannelCount, Segments];
        private readonly DispatcherTimer _timer;

        private Brush _litBrush = Brushes.Black;
        private Brush _dimBrush = Brushes.Transparent;
        private Brush _labelBrush = Brushes.Black;

        public ChannelBarGraph()
        {
            InitializeComponent();
            ApplyPalette(Color.FromRgb(0x24, 0x10, 0x00)); // 기본: SC 계열 주황 LCD의 진한 글자색
            BuildGrid();
            BuildLabels();

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) }; // ~30fps
            _timer.Tick += (_, _) => Tick();
            Loaded += (_, _) => { Layout(); _timer.Start(); };
            Unloaded += (_, _) => _timer.Stop();
            SizeChanged += (_, _) => Layout();
        }

        /// <summary>도트 색을 바꿉니다(LCD 종류마다 글자색이 다르므로).</summary>
        public void ApplyPalette(Color dotColor)
        {
            _litBrush = new SolidColorBrush(dotColor);
            _dimBrush = new SolidColorBrush(Color.FromArgb(0x38, dotColor.R, dotColor.G, dotColor.B));
            _labelBrush = new SolidColorBrush(dotColor);
            foreach (var child in ChannelLabels.Children)
                if (child is TextBlock tb) tb.Foreground = _labelBrush;
        }

        /// <summary>채널(0-15)의 활동 레벨을 0.0~1.0으로 즉시 끌어올립니다. (보통 velocity/127)</summary>
        public void SetLevel(int channel, double level)
        {
            if (channel < 0 || channel >= ChannelCount) return;
            double v = Math.Clamp(level, 0, 1);
            _levels[channel] = Math.Max(_levels[channel], v);
            if (v >= _peaks[channel])
            {
                _peaks[channel] = v;
                _peakHold[channel] = PeakHoldTicks;
            }
        }

        private void BuildGrid()
        {
            for (int ch = 0; ch < ChannelCount; ch++)
                for (int seg = 0; seg < Segments; seg++)
                {
                    var rect = new Rectangle { Fill = _dimBrush, RadiusX = 0.5, RadiusY = 0.5 };
                    _cells[ch, seg] = rect;
                    MeterCanvas.Children.Add(rect);
                }
        }

        private void BuildLabels()
        {
            ChannelLabels.ColumnDefinitions.Clear();
            ChannelLabels.Children.Clear();
            for (int ch = 0; ch < ChannelCount; ch++)
            {
                ChannelLabels.ColumnDefinitions.Add(new ColumnDefinition());
                var tb = new TextBlock
                {
                    Text = (ch + 1).ToString(),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 7.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = _labelBrush,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetColumn(tb, ch);
                ChannelLabels.Children.Add(tb);
            }
        }

        private void Layout()
        {
            double w = MeterCanvas.ActualWidth;
            double h = MeterCanvas.ActualHeight;
            if (w <= 0 || h <= 0) return;

            const double colGap = 2, rowGap = 1.5;
            double colW = (w - colGap * (ChannelCount - 1)) / ChannelCount;
            double rowH = (h - rowGap * (Segments - 1)) / Segments;
            if (colW <= 0 || rowH <= 0) return;

            for (int ch = 0; ch < ChannelCount; ch++)
                for (int seg = 0; seg < Segments; seg++)
                {
                    var rect = _cells[ch, seg];
                    rect.Width = colW;
                    rect.Height = rowH;
                    Canvas.SetLeft(rect, ch * (colW + colGap));
                    // seg=0이 맨 아래 (아래에서 차오르는 미터)
                    Canvas.SetTop(rect, h - (seg + 1) * rowH - seg * rowGap);
                }
        }

        private void Tick()
        {
            // 숨겨진 상태(Collapsed)로 시작하면 배치가 아직 안 잡혀 있을 수 있어서,
            // 화면에 나타난 뒤 첫 틱에서 한 번 더 배치를 잡아줍니다.
            if (_cells[0, 0].Width <= 0 && MeterCanvas.ActualWidth > 0) Layout();

            for (int ch = 0; ch < ChannelCount; ch++)
            {
                int lit = (int)Math.Round(_levels[ch] * (Segments - 1));
                int peakSeg = (int)Math.Round(_peaks[ch] * (Segments - 1));

                for (int seg = 0; seg < Segments; seg++)
                {
                    bool on =
                        seg == 0 ||                    // 맨 아랫줄: 파트 활성 표시(실기와 동일)
                        seg <= lit ||                  // 현재 레벨까지 차오른 막대
                        (peakSeg > 0 && seg == peakSeg); // 피크 홀드 도트
                    _cells[ch, seg].Fill = on ? _litBrush : _dimBrush;
                }

                _levels[ch] = Math.Max(0, _levels[ch] - DecayPerTick);

                if (_peakHold[ch] > 0) _peakHold[ch]--;
                else _peaks[ch] = Math.Max(_levels[ch], _peaks[ch] - PeakFallPerTick);
            }
        }
    }
}
