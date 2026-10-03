using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace SPWare.VirtualSoundCanvas.Localization
{
    /// <summary>
    /// 화면 언어(한국어/영어). 소스의 문구는 한국어 그대로 두고, 영어일 때만 아래 표(LocStrings)로 바꿔 보여 준다:
    ///  - XAML의 글자(TextBlock/Button/Expander 제목/툴팁)는 창이 만들어진 뒤 한 번 훑어서(TranslateTree) 바꾼다.
    ///  - 코드에서 만드는 문구(상태 로그, 콤보 항목, 대화상자 제목)는 T()를 거친다. 값이 끼는 문구는 "{0}" 자리표시가 있는
    ///    형식(템플릿)으로 등록해 두면 실제 문장에서 값을 뽑아 영어 문장에 다시 끼워 넣는다.
    /// 한국어 모드에서는 아무것도 하지 않는다(문구를 그대로 돌려줌). 언어는 시작할 때 한 번 정하고 실행 중에는 바꾸지 않는다.
    /// </summary>
    public static class Loc
    {
        private static bool _inited;
        public static bool English { get; private set; }

        private static readonly Regex Hangul = new("[가-힣]", RegexOptions.Compiled);
        private static readonly List<(Regex rx, string english)> Templates = new();
        private static readonly HashSet<string> Missing = new();

        /// <param name="preference">"auto"(윈도 언어를 따름), "ko", "en"</param>
        public static void Init(string? preference)
        {
            _inited = true;
            string p = (preference ?? "auto").Trim().ToLowerInvariant();
            English = p switch
            {
                "en" => true,
                "ko" => false,
                // 자동: 윈도 표시 언어 또는 지역 형식 중 하나라도 한국어면 한국어(표시 언어만 영어인 한국 PC도 한국어로 시작)
                _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ko"
                     && CultureInfo.CurrentCulture.TwoLetterISOLanguageName != "ko",
            };
            if (Environment.GetEnvironmentVariable("SPW_DUMP_STRINGS") is { Length: > 0 }) English = true; // 번역 누락 점검용

            foreach (var kv in LocStrings.Table)
            {
                if (!kv.Key.Contains('{')) continue;
                // "...{0}...{1}..." 템플릿 -> 정규식 (자리표시는 어떤 글자든 받는다)
                string pattern = Regex.Escape(kv.Key);
                pattern = Regex.Replace(pattern, @"\\\{(\d)\}", m => $"(?<g{m.Groups[1].Value}>.*?)");
                Templates.Add((new Regex("^" + pattern + "$", RegexOptions.Singleline | RegexOptions.Compiled), kv.Value));
            }
        }

        /// <summary>한국어 문구를 현재 언어로. 영어가 아니거나 한글이 없거나 표에 없으면 그대로 돌려준다.</summary>
        public static string T(string s)
        {
            if (!_inited) Init("auto");
            if (!English || string.IsNullOrEmpty(s) || !Hangul.IsMatch(s)) return s;

            if (LocStrings.Table.TryGetValue(s, out var en)) return en;

            foreach (var (rx, english) in Templates)
            {
                var m = rx.Match(s);
                if (!m.Success) continue;
                // 뽑아낸 값 안에 한글이 있으면(예: 예외 메시지) 그것도 번역
                return Regex.Replace(english, @"\{(\d)\}", g => T(m.Groups["g" + g.Groups[1].Value].Value));
            }

            Missing.Add(s);
            return s;
        }

        /// <summary>창 안의 모든 글자(TextBlock, Button/CheckBox 내용, Expander 제목, 툴팁 문자열)를 현재 언어로 바꾼다.</summary>
        public static void TranslateTree(DependencyObject root)
        {
            if (!_inited) Init("auto");
            if (!English) return;
            Walk(root);

            if (Environment.GetEnvironmentVariable("SPW_DUMP_STRINGS") is { Length: > 0 } path)
            {
                try { File.WriteAllLines(path, Missing.OrderBy(x => x, StringComparer.Ordinal), new UTF8Encoding(false)); }
                catch { /* 점검용이라 실패해도 무시 */ }
            }
        }

        /// <summary>
        /// 템플릿(DataTemplate 등)이 만든 요소는 논리 트리에 없어서 화면이 그려진 뒤에 비주얼 트리로 한 번 더 훑는다.
        /// (SC-55 패널의 화살표 버튼 툴팁이 그렇다.) 창의 Loaded 때 부른다.
        /// </summary>
        public static void TranslateVisualTree(DependencyObject root)
        {
            if (!_inited) Init("auto");
            if (!English) return;
            WalkVisual(root);
        }

        private static void WalkVisual(DependencyObject d)
        {
            Translate(d);
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) WalkVisual(System.Windows.Media.VisualTreeHelper.GetChild(d, i));
        }

        private static void Walk(DependencyObject d)
        {
            Translate(d);
            foreach (var child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject cd) Walk(cd);
        }

        private static void Translate(DependencyObject d)
        {
            switch (d)
            {
                case TextBlock tb:
                    {
                        string t = tb.Text;
                        string n = T(t);
                        if (!ReferenceEquals(n, t) && n != t) tb.Text = n;
                        break;
                    }
                case HeaderedContentControl h when h.Header is string hs:
                    h.Header = T(hs);
                    break;
                case ContentControl c when c.Content is string cs:
                    c.Content = T(cs);
                    break;
            }
            if (d is FrameworkElement fe && fe.ToolTip is string tip) fe.ToolTip = T(tip);
        }
    }
}
