using System;
using System.Windows;
using System.Windows.Media;

namespace SPWare.VirtualSoundCanvas.UI
{
    /// <summary>
    /// 실기 SPE3 LCD처럼 20자 x 1줄을 5x8 도트 셀로 직접 그리는 컨트롤.
    ///
    /// 글꼴(Consolas 등)로 그리면 글자가 매끈해서 실기 느낌이 안 나고, 특히 소리가 나는 파트를
    /// 보여주는 막대 문자(코드 1)를 실기처럼 표현할 수 없다. 여기서는 문자마다 5열 x 8행 도트를
    /// 켜고 끄는 방식으로 그린다: 켜진 도트는 밝은 연두(+번지는 빛), 꺼진 도트는 희미하게 보인다.
    ///
    /// 문자 코드 1(ACTIVE_PART_INDICATOR)은 실기 폰트의 막대 문자라서 5x7 전체가 켜진 블록으로 그린다.
    /// 0x20~0x7E는 일반 5x7 ASCII 도트 폰트를 쓰고, 그 밖의 코드는 빈칸이다.
    /// </summary>
    public sealed class DotMatrixLcd : FrameworkElement
    {
        // 주의: 아래 기본 브러시는 반드시 의존 속성(DependencyProperty) 등록보다 위에 선언해야 한다.
        // static 필드는 선언 순서대로 초기화되므로, 아래에 두면 속성의 기본값이 null이 되어 도트가 안 그려진다.
        private static readonly Brush DefaultOn = Freeze(new SolidColorBrush(Color.FromRgb(0xD8, 0xFF, 0x63)));
        private static readonly Brush DefaultHalo = Freeze(new SolidColorBrush(Color.FromArgb(0x38, 0xD8, 0xFF, 0x63)));
        private static readonly Brush DefaultOff = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x1B, 0x42, 0x08)));

        private static readonly Brush DefaultCursor = Freeze(new SolidColorBrush(Color.FromArgb(0xC0, 0x1B, 0x42, 0x08)));

        private static Brush Freeze(Brush b) { b.Freeze(); return b; }

        private const int CellW = 5;     // 글자 한 칸의 도트 가로 수
        private const int Rows = 8;      // 도트 세로 수(마지막 행은 g j p q y 의 꼬리 / 커서 줄)
        private const int Pitch = CellW + 1; // 글자 사이 빈 열 1개 포함
        private const byte ActivePartCode = 1;

        /// <summary>글자 수(가로 칸 수). SPE3 LCD는 20.</summary>
        public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
            nameof(Columns), typeof(int), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(20, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public int Columns
        {
            get => (int)GetValue(ColumnsProperty);
            set => SetValue(ColumnsProperty, value);
        }

        /// <summary>도트의 세로 간격 / 가로 간격. 1이면 정사각 배열(SPE3), SPE5 LCD는 글자가 더 길쭉해서 1.35 정도.</summary>
        public static readonly DependencyProperty DotAspectProperty = DependencyProperty.Register(
            nameof(DotAspect), typeof(double), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public double DotAspect
        {
            get => (double)GetValue(DotAspectProperty);
            set => SetValue(DotAspectProperty, value);
        }

        public static readonly DependencyProperty OnBrushProperty = DependencyProperty.Register(
            nameof(OnBrush), typeof(Brush), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(DefaultOn, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty HaloBrushProperty = DependencyProperty.Register(
            nameof(HaloBrush), typeof(Brush), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(DefaultHalo, FrameworkPropertyMetadataOptions.AffectsRender));
        public static readonly DependencyProperty OffBrushProperty = DependencyProperty.Register(
            nameof(OffBrush), typeof(Brush), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(DefaultOff, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>켜진 도트 색.</summary>
        public Brush? OnBrush { get => (Brush?)GetValue(OnBrushProperty); set => SetValue(OnBrushProperty, value); }
        /// <summary>켜진 도트 주변으로 번지는 빛(null이면 없음).</summary>
        public Brush? HaloBrush { get => (Brush?)GetValue(HaloBrushProperty); set => SetValue(HaloBrushProperty, value); }
        /// <summary>꺼진 도트 색(null이면 그리지 않음).</summary>
        public Brush? OffBrush { get => (Brush?)GetValue(OffBrushProperty); set => SetValue(OffBrushProperty, value); }

        /// <summary>
        /// true면 글자 아래에 얇은 줄 하나를 더 그린다. 실기 SPE3 LCD는 5x7 글자 바로 아래에 커서 줄(8번째 행)이 있어서
        /// 글자 밑에 얇은 선이 항상 보인다.
        /// </summary>
        public static readonly DependencyProperty CursorLineProperty = DependencyProperty.Register(
            nameof(CursorLine), typeof(bool), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public bool CursorLine
        {
            get => (bool)GetValue(CursorLineProperty);
            set => SetValue(CursorLineProperty, value);
        }

        public static readonly DependencyProperty CursorBrushProperty = DependencyProperty.Register(
            nameof(CursorBrush), typeof(Brush), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(DefaultCursor, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>커서 줄(대시) 색. 실기에서는 꺼진 도트와 같은 색이라 기본값을 꺼진 도트 기본색(DefaultOff)과 똑같이 둔다.</summary>
        public Brush? CursorBrush { get => (Brush?)GetValue(CursorBrushProperty); set => SetValue(CursorBrushProperty, value); }

        /// <summary>도트 한 개의 크기 / 도트 간격(피치). 실기 SPE3 LCD는 약 0.55~0.6이라 도트 사이 틈이 넓다. 기본 0.78.</summary>
        public static readonly DependencyProperty DotFillProperty = DependencyProperty.Register(
            nameof(DotFill), typeof(double), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(0.78, FrameworkPropertyMetadataOptions.AffectsRender));

        public double DotFill
        {
            get => (double)GetValue(DotFillProperty);
            set => SetValue(DotFillProperty, value);
        }

        /// <summary>도트 모서리 둥글기(도트 크기 대비). 실기 LCD의 도트는 각진 사각형에 가까워서 SPE3는 작게 쓴다.</summary>
        public static readonly DependencyProperty CornerRatioProperty = DependencyProperty.Register(
            nameof(CornerRatio), typeof(double), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(0.22, FrameworkPropertyMetadataOptions.AffectsRender));

        public double CornerRatio
        {
            get => (double)GetValue(CornerRatioProperty);
            set => SetValue(CornerRatioProperty, value);
        }

        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text), typeof(string), typeof(DotMatrixLcd),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }


        // ASCII 0x20~0x7E, 문자당 5바이트(열 단위, bit0 = 맨 위 행). 표준 5x7 도트 폰트.
        private static readonly byte[] Font =
        {
            0x00, 0x00, 0x00, 0x00, 0x00,  //  
            0x00, 0x00, 0x5F, 0x00, 0x00,  // !
            0x00, 0x07, 0x00, 0x07, 0x00,  // "
            0x14, 0x7F, 0x14, 0x7F, 0x14,  // #
            0x24, 0x2A, 0x7F, 0x2A, 0x12,  // $
            0x23, 0x13, 0x08, 0x64, 0x62,  // %
            0x36, 0x49, 0x56, 0x20, 0x50,  // &
            0x00, 0x08, 0x07, 0x03, 0x00,  // '
            0x00, 0x1C, 0x22, 0x41, 0x00,  // (
            0x00, 0x41, 0x22, 0x1C, 0x00,  // )
            0x2A, 0x1C, 0x7F, 0x1C, 0x2A,  // *
            0x08, 0x08, 0x3E, 0x08, 0x08,  // +
            0x00, 0x80, 0x70, 0x30, 0x00,  // ,
            0x08, 0x08, 0x08, 0x08, 0x08,  // -
            0x00, 0x00, 0x60, 0x60, 0x00,  // .
            0x20, 0x10, 0x08, 0x04, 0x02,  // /
            0x3E, 0x51, 0x49, 0x45, 0x3E,  // 0
            0x00, 0x42, 0x7F, 0x40, 0x00,  // 1
            0x72, 0x49, 0x49, 0x49, 0x46,  // 2
            0x21, 0x41, 0x49, 0x4D, 0x33,  // 3
            0x18, 0x14, 0x12, 0x7F, 0x10,  // 4
            0x27, 0x45, 0x45, 0x45, 0x39,  // 5
            0x3C, 0x4A, 0x49, 0x49, 0x31,  // 6
            0x41, 0x21, 0x11, 0x09, 0x07,  // 7
            0x36, 0x49, 0x49, 0x49, 0x36,  // 8
            0x46, 0x49, 0x49, 0x29, 0x1E,  // 9
            0x00, 0x00, 0x14, 0x00, 0x00,  // :
            0x00, 0x40, 0x34, 0x00, 0x00,  // ;
            0x00, 0x08, 0x14, 0x22, 0x41,  // <
            0x14, 0x14, 0x14, 0x14, 0x14,  // =
            0x00, 0x41, 0x22, 0x14, 0x08,  // >
            0x02, 0x01, 0x59, 0x09, 0x06,  // ?
            0x3E, 0x41, 0x5D, 0x59, 0x4E,  // @
            0x7C, 0x12, 0x11, 0x12, 0x7C,  // A
            0x7F, 0x49, 0x49, 0x49, 0x36,  // B
            0x3E, 0x41, 0x41, 0x41, 0x22,  // C
            0x7F, 0x41, 0x41, 0x41, 0x3E,  // D
            0x7F, 0x49, 0x49, 0x49, 0x41,  // E
            0x7F, 0x09, 0x09, 0x09, 0x01,  // F
            0x3E, 0x41, 0x41, 0x51, 0x73,  // G
            0x7F, 0x08, 0x08, 0x08, 0x7F,  // H
            0x00, 0x41, 0x7F, 0x41, 0x00,  // I
            0x20, 0x40, 0x41, 0x3F, 0x01,  // J
            0x7F, 0x08, 0x14, 0x22, 0x41,  // K
            0x7F, 0x40, 0x40, 0x40, 0x40,  // L
            0x7F, 0x02, 0x1C, 0x02, 0x7F,  // M
            0x7F, 0x04, 0x08, 0x10, 0x7F,  // N
            0x3E, 0x41, 0x41, 0x41, 0x3E,  // O
            0x7F, 0x09, 0x09, 0x09, 0x06,  // P
            0x3E, 0x41, 0x51, 0x21, 0x5E,  // Q
            0x7F, 0x09, 0x19, 0x29, 0x46,  // R
            0x26, 0x49, 0x49, 0x49, 0x32,  // S
            0x03, 0x01, 0x7F, 0x01, 0x03,  // T
            0x3F, 0x40, 0x40, 0x40, 0x3F,  // U
            0x1F, 0x20, 0x40, 0x20, 0x1F,  // V
            0x3F, 0x40, 0x38, 0x40, 0x3F,  // W
            0x63, 0x14, 0x08, 0x14, 0x63,  // X
            0x03, 0x04, 0x78, 0x04, 0x03,  // Y
            0x61, 0x59, 0x49, 0x4D, 0x43,  // Z
            0x00, 0x7F, 0x41, 0x41, 0x41,  // [
            0x02, 0x04, 0x08, 0x10, 0x20,  // backslash
            0x00, 0x41, 0x41, 0x41, 0x7F,  // ]
            0x04, 0x02, 0x01, 0x02, 0x04,  // ^
            0x40, 0x40, 0x40, 0x40, 0x40,  // _
            0x00, 0x03, 0x07, 0x08, 0x00,  // `
            0x20, 0x54, 0x54, 0x78, 0x40,  // a
            0x7F, 0x28, 0x44, 0x44, 0x38,  // b
            0x38, 0x44, 0x44, 0x44, 0x28,  // c
            0x38, 0x44, 0x44, 0x28, 0x7F,  // d
            0x38, 0x54, 0x54, 0x54, 0x18,  // e
            0x00, 0x08, 0x7E, 0x09, 0x02,  // f
            0x18, 0xA4, 0xA4, 0x9C, 0x78,  // g
            0x7F, 0x08, 0x04, 0x04, 0x78,  // h
            0x00, 0x44, 0x7D, 0x40, 0x00,  // i
            0x20, 0x40, 0x40, 0x3D, 0x00,  // j
            0x7F, 0x10, 0x28, 0x44, 0x00,  // k
            0x00, 0x41, 0x7F, 0x40, 0x00,  // l
            0x7C, 0x04, 0x78, 0x04, 0x78,  // m
            0x7C, 0x08, 0x04, 0x04, 0x78,  // n
            0x38, 0x44, 0x44, 0x44, 0x38,  // o
            0xFC, 0x18, 0x24, 0x24, 0x18,  // p
            0x18, 0x24, 0x24, 0x18, 0xFC,  // q
            0x7C, 0x08, 0x04, 0x04, 0x08,  // r
            0x48, 0x54, 0x54, 0x54, 0x24,  // s
            0x04, 0x04, 0x3F, 0x44, 0x24,  // t
            0x3C, 0x40, 0x40, 0x20, 0x7C,  // u
            0x1C, 0x20, 0x40, 0x20, 0x1C,  // v
            0x3C, 0x40, 0x30, 0x40, 0x3C,  // w
            0x44, 0x28, 0x10, 0x28, 0x44,  // x
            0x4C, 0x90, 0x90, 0x90, 0x7C,  // y
            0x44, 0x64, 0x54, 0x4C, 0x44,  // z
            0x00, 0x08, 0x36, 0x41, 0x00,  // {
            0x00, 0x00, 0x77, 0x00, 0x00,  // |
            0x00, 0x41, 0x36, 0x08, 0x00,  // }
            0x02, 0x01, 0x02, 0x04, 0x02,  // ~
        };

        protected override Size MeasureOverride(Size availableSize)
        {
            int cols = Math.Max(1, Columns);
            double w = double.IsInfinity(availableSize.Width) ? 380 : availableSize.Width;
            double dot = w / (cols * Pitch);
            return new Size(w, dot * Math.Max(0.5, DotAspect) * ((CursorLine ? Rows + 1 : Rows) + 0.4));
        }

        protected override void OnRender(DrawingContext dc)
        {
            int cols = Math.Max(1, Columns);
            double dotX = ActualWidth / (cols * Pitch);      // 도트 가로 간격
            if (dotX <= 0) return;
            double dotY = dotX * Math.Max(0.5, DotAspect);    // 도트 세로 간격

            double size = dotX * Math.Clamp(DotFill, 0.3, 1.0);  // 도트 하나의 크기(정사각, 나머지는 틈)
            double insetX = (dotX - size) / 2;
            double insetY = (dotY - size) / 2;
            double radius = size * Math.Clamp(CornerRatio, 0, 0.5);
            bool cursorLine = CursorLine;
            int totalRows = cursorLine ? Rows + 1 : Rows;   // 커서 모드: 글자 7행 + 빈 행(8번째) + 대시 행(9번째)
            double yOffset = (ActualHeight - totalRows * dotY) / 2;
            Brush? cursor = CursorBrush;
            string text = Text ?? string.Empty;
            Brush? on = OnBrush, halo = HaloBrush, off = OffBrush;

            for (int c = 0; c < cols; c++)
            {
                char ch = c < text.Length ? text[c] : ' ';
                for (int x = 0; x < CellW; x++)
                {
                    int bits = ColumnBits(ch, x);
                    double px = (c * Pitch + x) * dotX + insetX;
                    for (int y = 0; y < Rows; y++)
                    {
                        bool lit = ((bits >> y) & 1) != 0;
                        var rect = new Rect(px, yOffset + y * dotY + insetY, size, size);
                        if (lit)
                        {
                            if (halo != null)
                                dc.DrawRoundedRectangle(halo, null, Rect.Inflate(rect, dotX * 0.55, dotX * 0.55), radius * 2, radius * 2);
                            dc.DrawRoundedRectangle(on, null, rect, radius, radius);
                        }
                        // 실기 LCD(SPE3): 8번째 행(g j p q y 의 꼬리 자리)은 켜졌을 때만 보이고, 꺼진 도트는 그리지 않는다.
                        // 실기 사진에서 꺼진 도트 격자는 7행까지만 보인다.
                        else if (off != null && !(cursorLine && y == Rows - 1))
                        {
                            dc.DrawRoundedRectangle(off, null, rect, radius, radius);
                        }
                    }

                    // 커서 줄: 글자 7행 아래, 빈 행 하나를 건너뛴 9번째 행에 칸마다 도트 5개.
                    // 실기 LCD에서 이 줄의 도트는 글자 도트와 **같은 크기**이고 색만 흐리다(사용자 지적 + 이미지 15 측정:
                    // 대시 두께가 도트 행 한 줄과 같음). 납작한 막대가 아니라 정사각 도트를 흐린 색으로 그린다.
                    if (cursorLine && cursor != null)
                    {
                        var dot = new Rect(px, yOffset + Rows * dotY + insetY, size, size);
                        dc.DrawRoundedRectangle(cursor, null, dot, radius, radius);
                    }
                }
            }
        }

        private static int ColumnBits(char ch, int column)
        {
            if (ch == ActivePartCode) return 0x7F;        // 막대 문자: 5x7 전체가 켜진 블록
            if (ch < 0x20 || ch > 0x7E) return 0;
            return Font[(ch - 0x20) * CellW + column];
        }
    }
}
