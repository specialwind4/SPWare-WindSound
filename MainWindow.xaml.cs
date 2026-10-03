using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using SPWare.VirtualSoundCanvas.Audio;
using SPWare.VirtualSoundCanvas.Config;
using SPWare.VirtualSoundCanvas.Engines;
using SPWare.VirtualSoundCanvas.Localization;
using SPWare.VirtualSoundCanvas.Midi;

namespace SPWare.VirtualSoundCanvas
{
    public partial class MainWindow : Window
    {
        private readonly MidiRouter _router = new();
        private readonly Mt32Engine _mt32 = new();
        private readonly Sc55Engine _sc55 = new();
        private RawMidiIn? _midiIn;

        // 엔진들이 만든 소리를 실제 스피커로 내보내는 부분.
        private readonly EngineAudioMixer _mixer = new();
        private WasapiOut? _audioOutput;
        private readonly DispatcherTimer _displayPollTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

        private readonly AppSettings _settings;
        /// <summary>창이 내용에 맞춰 늘어나는 최대 높이(DIP). 설정을 다 펴면 이보다 길어져서 스크롤이 생긴다.</summary>
        private const double MaxWindowHeight = 860;

        private bool _uiReady; // 설정 로드 중에 이벤트가 튀는 걸 막습니다
        private bool _skipTabSwitchOnce; // 시작할 때 저장된 MIDI 종류를 복원하는 첫 변경은 화면 탭을 바꾸지 않는다(마지막으로 보던 탭 유지)
        private bool _applyingKind; // 종류를 적용하면서 콤보를 바꿀 때, 그 변경을 '직접 설정'으로 오해하지 않게

        public MainWindow()
        {
            InitializeComponent();
            _settings = AppSettings.Load();
            Loc.Init(_settings.Language);   // 화면 언어는 시작할 때 한 번 정한다
            Loc.TranslateTree(this);        // XAML의 글자를 영어로(한국어면 아무 일도 안 함)
            MaxHeight = Math.Min(SystemParameters.WorkArea.Height, MaxWindowHeight); // 내용이 이보다 길면 스크롤
            RestoreWindowSize();
            SetupSections();

            Loaded += (_, _) =>
            {
                Loc.TranslateVisualTree(this); // 템플릿이 만든 요소(화살표 버튼 툴팁 등)
                PopulateDevices();
                ApplySettingsToUi();
                _uiReady = true;
                if (_settings.AutoStart) StartAll();
            };

            _router.RegisterEngine(_mt32);
            _router.RegisterEngine(_sc55);

            // 믹서에도 등록해야 실제로 소리가 납니다 (라우터 등록과는 별개).
            // 엔진별 기본 크기 보정(§2.64): 같은 피아노 코드를 엔진에 넣어 잰 값(SPE3 -28.9 / SPE5 -35.3 dBFS)을
            // 공통 목표 -33.0 dBFS에 맞춘다(메인 버전과 같은 값이라 두 버전의 소리 크기가 같다).
            _mixer.AddEngine(_mt32, 0.624f);  // -4.1 dB
            _mixer.AddEngine(_sc55, 1.303f);  // +2.3 dB

            // --- 화면 배선 ---
            _mt32.LcdTextChanged += (_, text) => Mt32Panel.OnEngineLcdTextChanged(this, text);
            Mt32Panel.Attach(_router, _mt32);
            // SPE3 패널의 SELECT/VOLUME 노브 = SPE3 마스터 볼륨(SysEx). 액정의 Vol 값도 같이 바뀐다.
            Mt32Panel.MasterVolumeChanged += v => _mt32.SetMasterVolume(v);
            // PART로 고른 파트의 음량(VOLUME 버튼 + 노브), 그리고 그때 액정에 띄우는 안내 문구
            Mt32Panel.PartVolumeChanged += (part, v) => _mt32.SetPartVolume(part, v);
            Mt32Panel.CustomMessageRequested += msg => _mt32.ShowCustomMessage(msg);
            // RESET: Munt 초기화 후 노브 값으로 마스터 볼륨을 다시 맞춘다
            Mt32Panel.ResetRequested += () =>
            {
                _mt32.Reset();
                _mt32.SetMasterVolume(Mt32Panel.MasterVolume);
                Log("SPE3 리셋");
            };
            Sc55Panel.Attach(_router, _sc55);
            // 패널의 VOLUME 노브를 돌리면 SPE5 음량 슬라이더가 따라가고, 슬라이더를 움직이면 노브도 돌아간다
            Sc55Panel.VolumeKnobChanged += v =>
            {
                if (Math.Abs(Sc55VolumeSlider.Value - v * 100) > 0.01) Sc55VolumeSlider.Value = v * 100;
            };
            EngineCombo.ItemsSource = new[] { _mt32.Name, _sc55.Name };
            EngineCombo.SelectedIndex = 0;
            ChannelCombo.ItemsSource = Enumerable.Range(0, 16);
            ChannelCombo.SelectedIndex = 0;

            RomSetCombo.ItemsSource = new[] { Loc.T("구형 MT-32 (1987, v1.07)"), Loc.T("신형 MT-32 (1988, patched)"), "CM-32L (1989, v1.02)" };
            RomSetCombo.SelectionChanged += OnRomSetChanged;

            DefaultEngineCombo.ItemsSource = new[] { _mt32.Name, _sc55.Name };
            DefaultEngineCombo.SelectionChanged += (_, _) =>
            {
                int i = Math.Max(DefaultEngineCombo.SelectedIndex, 0);
                _router.ActiveEngine = EngineByIndex(i);
                if (_uiReady) _settings.DefaultEngineIndex = i;
                if (_uiReady && !_applyingKind) MarkCustomFromUi();
            };

            // ---- MIDI 종류(프리셋) + 자동 감지 ----
            _router.KindToEngine = kind => kind switch
            {
                MidiKind.Mt32 => _mt32,
                MidiKind.GmGs => _sc55,
                _ => null,
            };
            _router.KindChanged += (_, e) =>
                Dispatcher.BeginInvoke(new Action(() => OnKindChanged(e.kind, e.automatic)));
            KindCombo.ItemsSource = new[]
            {
                Loc.T("직접 설정"),
                Loc.T("SPE3 (게임의 SPE3 음악)"),
                Loc.T("GM · GS (SPE5 에뮬레이션)"),
            };
            KindCombo.SelectedIndex = 0;
            KindCombo.SelectionChanged += OnKindComboChanged;
            AutoKindCheck.Checked += (_, _) => _router.AutoDetectKind = true;
            AutoKindCheck.Unchecked += (_, _) => _router.AutoDetectKind = false;

            BuildEngineTabs();

            // 엔진들이 LCD 텍스트를 실제로 내주는 경우에만 의미가 있고, 못 내면
            // Poll 메서드들이 즉시 리턴하므로 항상 켜둬도 부담이 없습니다.
            // (MIDI 메시지마다 읽지 않고 여기서 주기적으로만 읽는 게 핵심입니다.)
            _displayPollTimer.Tick += (_, _) =>
            {
                _mt32.PollDisplayState();
                _sc55.PollStatusText();
            };
            _displayPollTimer.Start();
        }

        // ---------------------------------------------------------------
        // 접었다 펼 수 있는 설정 섹션
        // ---------------------------------------------------------------

        // ---------------------------------------------------------------
        // 창 크기 기억
        // ---------------------------------------------------------------

        /// <summary>
        /// 지난번에 늘려 둔 폭(과 사용자가 직접 정한 높이)을 되살린다. 최소 폭보다 작거나 화면보다 크면 잘라서 쓴다.
        /// 높이는 사용자가 직접 정한 경우에만 저장/복원한다: 그렇지 않으면 내용에 맞춰 자동으로 줄고 느는 기본 동작을 유지한다.
        /// </summary>
        private void RestoreWindowSize()
        {
            var work = SystemParameters.WorkArea;
            if (_settings.WindowWidth >= MinWidth)
                Width = Math.Min(_settings.WindowWidth, work.Width);

            if (_settings.WindowHeight >= MinHeight)
            {
                SizeToContent = SizeToContent.Manual; // 사용자가 정한 높이를 그대로 쓴다(설정을 접고 펼 때 다시 자동 맞춤으로 돌아간다)
                Height = Math.Min(_settings.WindowHeight, work.Height);
            }

            RestoreWindowPosition();
        }

        /// <summary>
        /// 종료 전 위치로 연다. 모니터를 빼는 등으로 저장된 위치가 지금 화면 밖이면(제목 표시줄을 잡을 수 없으면)
        /// 쓰지 않고 윈도우 기본 위치로 둔다.
        /// </summary>
        private void RestoreWindowPosition()
        {
            if (_settings.WindowLeft is not double left || _settings.WindowTop is not double top) return;

            // 창의 제목 표시줄 띠(위쪽 40px)가 어느 화면 영역과 50px 이상 겹쳐야 잡을 수 있는 위치로 본다
            double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            double overlapW = Math.Min(left + Width, vx + vw) - Math.Max(left, vx);
            double overlapH = Math.Min(top + 40, vy + vh) - Math.Max(top, vy);
            if (overlapW < 50 || overlapH < 20) return;

            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        private void SaveWindowSize()
        {
            // 최대화/최소화 상태에서는 창의 "원래 크기"(RestoreBounds)를 저장한다
            var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (bounds.Width >= MinWidth) _settings.WindowWidth = bounds.Width;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            // 자동 맞춤(SizeToContent=Height) 상태면 높이는 저장하지 않는다
            _settings.WindowHeight = SizeToContent == SizeToContent.Manual && bounds.Height >= MinHeight ? bounds.Height : 0;
        }

        /// <summary>섹션마다 접힘 상태를 설정 파일에 기억합니다(다음 실행 때도 그대로).</summary>
        private void SetupSections()
        {
            var sections = new (Expander Expander, string Key)[]
            {
                (SecAll, "all"), (SecRom, "rom"), (SecChannel, "channel"), (SecMidi, "midi"),
                (SecSc55Rom, "sc55rom"), (SecAudio, "audio"), (SecLog, "log"),
            };
            // 도움말은 처음에 접혀 있어야 하므로 "접힌 목록"이 아니라 별도 값으로 기억한다
            SecHelp.IsExpanded = _settings.HelpExpanded && _settings.CollapsedSections.Contains("all"); // 설정이 펼쳐져 있으면 도움말은 접힌 채로 시작
            SecHelp.Expanded += (_, _) => { _settings.HelpExpanded = true; FitWindowToContent(force: true); };
            SecHelp.Collapsed += (_, _) => { _settings.HelpExpanded = false; FitWindowToContent(force: true); };

            // 설정과 도움말은 제목 줄에 나란히 있고 하나를 펼치면 다른 하나는 접힌다(위의 두 핸들러 뒤에 연결)
            SecAll.Expanded += (_, _) => SecHelp.IsExpanded = false;
            SecHelp.Expanded += (_, _) => SecAll.IsExpanded = false;

            foreach (var (expander, key) in sections)
            {
                expander.IsExpanded = !_settings.CollapsedSections.Contains(key);
                expander.Expanded += (_, _) => { _settings.CollapsedSections.Remove(key); FitWindowToContent(force: true); };
                expander.Collapsed += (_, _) =>
                {
                    if (!_settings.CollapsedSections.Contains(key)) _settings.CollapsedSections.Add(key);
                    FitWindowToContent(force: true);
                };
            }
        }

        /// <summary>
        /// 창 높이를 내용에 맞춘다: 설정을 접으면 창도 줄고, 펴면 (작업 영역 높이까지) 늘어난다.
        /// SizeToContent는 사용자가 창 크기를 직접 조절하면 풀려 버리므로, 접고 펼 때마다 다시 걸어 준다.
        /// 늘어난 창이 화면 아래로 삐져나가면 위로 끌어올린다.
        /// force=false(기기 탭 전환 등)는 사용자가 정한 높이를 존중하고, force=true(설정 접기/펴기)는 다시 내용에 맞춘다.
        /// </summary>
        private void FitWindowToContent(bool force = false)
        {
            if (!IsLoaded) return;
            // 사용자가 높이를 직접 조절했거나(SizeToContent가 Manual로 바뀜) 저장된 높이를 복원한 상태면, 기기 탭을 바꾼다고 그 높이를
            // 덮어쓰지 않는다. 설정을 접거나 펼 때(force)만 다시 내용에 맞춘다.
            if (!force && SizeToContent == SizeToContent.Manual) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var workArea = SystemParameters.WorkArea;
                MaxHeight = Math.Min(workArea.Height, MaxWindowHeight);
                SizeToContent = SizeToContent.Manual;
                SizeToContent = SizeToContent.Height;
                UpdateLayout();
                if (Top + ActualHeight > workArea.Bottom)
                    Top = Math.Max(workArea.Top, workArea.Bottom - ActualHeight);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---------------------------------------------------------------
        // 설정 <-> UI
        // ---------------------------------------------------------------

        private void ApplySettingsToUi()
        {
            RomSetCombo.SelectedIndex = Math.Clamp(_settings.Mt32RomSetIndex, 0, 2);
            Mt32RomPathBox.Text = _settings.GetMt32Folder();
            Sc55RomPathBox.Text = _settings.Sc55RomFolder;

            MasterVolumeSlider.Value = _settings.MasterVolume * 100;
            Mt32VolumeSlider.Value = _settings.Mt32Volume * 100;
            Sc55VolumeSlider.Value = _settings.Sc55Volume * 100;
            AutoStartCheck.IsChecked = _settings.AutoStart;

            LanguageCombo.ItemsSource = new[] { "Auto (Windows language / 윈도 언어)", "한국어", "English" };
            LanguageCombo.SelectedIndex = _settings.Language switch { "ko" => 1, "en" => 2, _ => 0 };

            DefaultEngineCombo.SelectedIndex = Math.Clamp(_settings.DefaultEngineIndex, 0, 1);

            SelectComboByName(MidiInCombo, _settings.MidiInDeviceName);

            // 저장된 채널 배정 복원
            foreach (var kv in _settings.ChannelAssignments)
            {
                if (!int.TryParse(kv.Key, out int ch)) continue;
                _router.AssignChannel(ch, EngineByIndex(kv.Value));
            }

            if (_settings.ChannelAssignments.Count > 0)
                Log($"저장된 채널 배정 {_settings.ChannelAssignments.Count}개를 불러왔습니다");

            // MIDI 종류: 저장된 종류가 있으면 시작할 때 그대로 적용, 자동 감지 체크 복원
            AutoKindCheck.IsChecked = _settings.AutoDetectKind;
            var savedKind = (MidiKind)Math.Clamp(_settings.MidiKindIndex, 0, 2);
            _applyingKind = true;
            KindCombo.SelectedIndex = (int)savedKind;
            _applyingKind = false;
            if (savedKind != MidiKind.Custom)
            {
                _skipTabSwitchOnce = true; // 소리 경로만 복원하고, 화면 탭은 위에서 복원한 마지막 탭 그대로 둔다
                _router.ApplyKind(savedKind, automatic: false);
            }

            // 탭/맵 복원
            var tabs = EngineTabGrid.Children.OfType<RadioButton>().ToList();
            int idx = Math.Clamp(_settings.ActiveEngineIndex, 0, tabs.Count - 1);
            if (tabs.Count > idx) tabs[idx].IsChecked = true;

            ApplyVolumes();
        }

        private void CaptureUiToSettings()
        {
            _settings.Mt32RomSetIndex = Math.Max(RomSetCombo.SelectedIndex, 0);
            SetMt32FolderForCurrentSet(Mt32RomPathBox.Text);
            _settings.Sc55RomFolder = Sc55RomPathBox.Text;

            _settings.MasterVolume = MasterVolumeSlider.Value / 100.0;
            _settings.Mt32Volume = Mt32VolumeSlider.Value / 100.0;
            _settings.Sc55Volume = Sc55VolumeSlider.Value / 100.0;
            _settings.AutoStart = AutoStartCheck.IsChecked == true;
            _settings.AutoDetectKind = AutoKindCheck.IsChecked == true;
            _settings.Language = LanguageCombo.SelectedIndex switch { 1 => "ko", 2 => "en", _ => "auto" };

            _settings.MidiInDeviceName = MidiInCombo.SelectedItem as string ?? "";
        }

        private void SetMt32FolderForCurrentSet(string path)
        {
            switch (RomSetCombo.SelectedIndex)
            {
                case 1: _settings.Mt32NewRomFolder = path; break;
                case 2: _settings.Cm32LRomFolder = path; break;
                default: _settings.Mt32OldRomFolder = path; break;
            }
        }

        private static void SelectComboByName(ComboBox combo, string name)
        {
            if (string.IsNullOrEmpty(name) || combo.ItemsSource is null) return;
            var items = combo.ItemsSource.Cast<object>().Select(o => o?.ToString() ?? "").ToList();
            int i = items.FindIndex(x => x == name);
            if (i >= 0) combo.SelectedIndex = i;
        }

        private ISynthEngine EngineByIndex(int index) => index switch
        {
            1 => _sc55,
            _ => _mt32,
        };

        // ---------------------------------------------------------------
        // 탭
        // ---------------------------------------------------------------

        private void BuildEngineTabs()
        {
            AddEngineTab("SPE3", _mt32, isDefault: true);
            AddEngineTab("SPE5", _sc55, isDefault: false);
        }

        private void AddEngineTab(string label, ISynthEngine engine, bool isDefault)
        {
            var tab = new RadioButton
            {
                Content = label,
                GroupName = "EngineTab",
                Style = (Style)FindResource("EngineTab"),
                IsChecked = isDefault,
            };
            tab.Checked += (_, _) => ShowEngine(engine);
            EngineTabGrid.Children.Add(tab);
            if (isDefault) ShowEngine(engine);
        }

        /// <summary>
        /// 탭은 "화면에 어느 장치를 띄울지"만 바꿉니다. 소리 경로(라우터)는 절대
        /// 건드리지 않습니다.
        ///
        /// 예전에는 탭을 바꾸면 _router.ActiveEngine도 같이 바뀌었는데, 그러면
        /// SC-88Pro 탭을 누르는 순간 배정 안 된 모든 채널이 "외부 전달" 엔진으로
        /// 넘어갑니다. 그 전달 대상이 입력으로 쓰는 loopMIDI 포트와 같으면
        /// 들어온 MIDI가 같은 포트로 되돌아가 무한 루프가 됩니다. 화면 전환이
        /// 소리 경로를 바꾸는 것 자체가 예상 밖의 동작이라 분리했습니다.
        /// </summary>
        private void ShowEngine(ISynthEngine engine)
        {
            Mt32Panel.Visibility = engine == _mt32 ? Visibility.Visible : Visibility.Collapsed;
            Sc55Panel.Visibility = engine == _sc55 ? Visibility.Visible : Visibility.Collapsed;

            // 장치별 전용 설정 카드도 같이 전환합니다.
            RomSetCard.Visibility = engine == _mt32 ? Visibility.Visible : Visibility.Collapsed;
            FitWindowToContent();
            Sc55RomCard.Visibility = engine == _sc55 ? Visibility.Visible : Visibility.Collapsed;

            if (_uiReady)
                _settings.ActiveEngineIndex = engine == _sc55 ? 1 : 0;
        }

        // ---------------------------------------------------------------
        // 이벤트 핸들러
        // ---------------------------------------------------------------

        private void PopulateDevices()
        {
            try
            {
                MidiInCombo.ItemsSource = RawMidiIn.ListDevices();
            }
            catch (Exception ex)
            {
                Log($"MIDI 장치 목록 조회 실패: {ex.Message}");
            }
        }

        private void OnRomSetChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_uiReady) return;
            _settings.Mt32RomSetIndex = Math.Max(RomSetCombo.SelectedIndex, 0);
            Mt32RomPathBox.Text = _settings.GetMt32Folder();
        }

        private void OnBrowseMt32Rom(object sender, RoutedEventArgs e)
        {
            var path = PickFolder(Loc.T("MT-32 / CM-32L ROM 폴더를 고르세요"), Mt32RomPathBox.Text);
            if (path is null) return;
            Mt32RomPathBox.Text = path;
            SetMt32FolderForCurrentSet(path);
        }

        private void OnBrowseSc55Rom(object sender, RoutedEventArgs e)
        {
            var path = PickFolder(Loc.T("SC-55mkII ROM 폴더를 고르세요"), Sc55RomPathBox.Text);
            if (path is null) return;
            Sc55RomPathBox.Text = path;
            _settings.Sc55RomFolder = path;
        }

        /// <summary>
        /// .NET 8 WPF에 기본 포함된 OpenFolderDialog를 씁니다
        /// (예전 WinForms FolderBrowserDialog를 끌어올 필요가 없습니다).
        /// </summary>
        private static string? PickFolder(string title, string initial)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = title };
            if (!string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial))
                dlg.InitialDirectory = initial;
            return dlg.ShowDialog() == true ? dlg.FolderName : null;
        }

        private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (!_uiReady) return;
            ApplyVolumes();
        }

        private void ApplyVolumes()
        {
            _mixer.MasterVolume = (float)(MasterVolumeSlider.Value / 100.0);
            _mixer.SetEngineVolume(_mt32, (float)(Mt32VolumeSlider.Value / 100.0));
            _mixer.SetEngineVolume(_sc55, (float)(Sc55VolumeSlider.Value / 100.0));

            MasterVolumeText.Text = ((int)MasterVolumeSlider.Value).ToString();
            Mt32VolumeText.Text = ((int)Mt32VolumeSlider.Value).ToString();
            Sc55VolumeText.Text = ((int)Sc55VolumeSlider.Value).ToString();
            Sc55Panel.SetVolumeKnob(Sc55VolumeSlider.Value / 100.0);
        }

        private void OnAssignChannel(object sender, RoutedEventArgs e)
        {
            if (ChannelCombo.SelectedItem is not int channel) return;
            int engineIndex = Math.Max(EngineCombo.SelectedIndex, 0);
            var engine = EngineByIndex(engineIndex);
            _router.AssignChannel(channel, engine);
            _settings.SetChannelAssignment(channel, engineIndex);
            Log($"채널 {channel} → {engine.Name} 배정");
            MarkCustomFromUi();
        }

        // ---------------------------------------------------------------
        // MIDI 종류(프리셋) + 자동 감지
        // ---------------------------------------------------------------

        private static int EngineIndexOf(MidiKind kind) => kind switch
        {
            MidiKind.Mt32 => 0,
            MidiKind.GmGs => 1,
            _ => -1,
        };

        /// <summary>콤보에서 종류를 골랐을 때: 직접 설정이면 그대로 두고, 아니면 배정과 기본 엔진을 그 종류로 바꾼다.</summary>
        private void OnKindComboChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_applyingKind || !_uiReady) return;
            var kind = (MidiKind)Math.Max(KindCombo.SelectedIndex, 0);
            if (kind == MidiKind.Custom)
            {
                _router.MarkCustom();
                _settings.MidiKindIndex = 0;
                Log("MIDI 종류: 직접 설정 (채널 배정과 기본 엔진을 직접 관리합니다)");
                return;
            }
            _router.ApplyKind(kind, automatic: false);
        }

        /// <summary>종류가 적용됐을 때(콤보 선택 또는 자동 감지) 화면과 설정을 맞춘다. UI 스레드에서 호출.</summary>
        private void OnKindChanged(MidiKind kind, bool automatic)
        {
            _applyingKind = true;
            try
            {
                KindCombo.SelectedIndex = (int)kind;
                int engineIndex = EngineIndexOf(kind);
                if (engineIndex >= 0)
                {
                    DefaultEngineCombo.SelectedIndex = engineIndex;
                    // 보이는 기기도 그 종류에 맞춰 준다 (소리 경로와는 무관한 화면 전환).
                    // 단, 시작할 때 저장된 종류를 복원하는 첫 변경은 건너뛴다: 사용자가 마지막으로 보던 탭이 우선이다.
                    if (_skipTabSwitchOnce)
                    {
                        _skipTabSwitchOnce = false;
                    }
                    else
                    {
                        var tabs = EngineTabGrid.Children.OfType<RadioButton>().ToList();
                        if (engineIndex < tabs.Count) tabs[engineIndex].IsChecked = true;
                    }
                }
                _settings.MidiKindIndex = (int)kind;
                _settings.ChannelAssignments.Clear();
            }
            finally
            {
                _applyingKind = false;
            }

            string name = MidiKindDetector.Describe(kind);
            Log(automatic
                ? $"MIDI 종류 자동 감지: {name} → 채널 배정을 자동으로 바꿨습니다"
                : $"MIDI 종류 '{name}' 적용 (개별 채널 배정 해제, 기본 엔진 변경)");
        }

        /// <summary>사용자가 채널 배정/기본 엔진을 손으로 바꿨을 때: 종류를 '직접 설정'으로 돌린다.</summary>
        private void MarkCustomFromUi()
        {
            _router.MarkCustom();
            _settings.MidiKindIndex = 0;
            if (KindCombo.SelectedIndex != 0)
            {
                _applyingKind = true;
                KindCombo.SelectedIndex = 0;
                _applyingKind = false;
            }
        }

        private void OnStart(object sender, RoutedEventArgs e) => StartAll();

        private void StartAll()
        {
            CaptureUiToSettings();

            // 각 구성요소를 독립적으로 시도 - 하나가 실패해도 나머지는 계속 진행됩니다.
            StartMt32();
            StartSc55();
            StartAudio();
            StartMidiInput();
        }

        private void StartMt32()
        {
            try
            {
                string folder = Mt32RomPathBox.Text;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    Log("SPE3 건너뜀: MT-32 ROM 폴더가 지정되지 않았습니다 (SPE3 탭에서 폴더를 고르세요)");
                    return;
                }

                bool isCm32L = RomSetCombo.SelectedIndex == 2;
                _mt32.RomSet = RomSetCombo.SelectedIndex switch
                {
                    1 => Mt32RomSet.Mt32New,
                    2 => Mt32RomSet.Cm32L,
                    _ => Mt32RomSet.Mt32Old,
                };

                // 파일명이 표준과 다를 수 있으니 폴더 안에서 찾아냅니다.
                string? control = FindRom(folder, isCm32L ? "CM32L_CONTROL" : "MT32_CONTROL", "CONTROL");
                string? pcm = FindRom(folder, isCm32L ? "CM32L_PCM" : "MT32_PCM", "PCM");

                if (control is null || pcm is null)
                {
                    string missing = control is null && pcm is null ? "CONTROL과 PCM 둘 다"
                                   : control is null ? "CONTROL" : "PCM";
                    Log($"SPE3 건너뜀: MT-32 {missing} ROM을 찾지 못했습니다. 지정한 폴더: {folder}");
                    LogFolderContents(folder);
                    return;
                }

                Log($"MT-32 ROM 발견 - CONTROL: {Path.GetFileName(control)}, PCM: {Path.GetFileName(pcm)}");

                _mt32.ControlRomPath = control;
                _mt32.PcmRomPath = pcm;
                _mt32.Open();
                _mt32.SetMasterVolume(_settings.Mt32MasterVolume);   // 지난번 노브 위치 복원(액정 Vol도 따라 바뀐다)
                Mt32Panel.SetMasterVolume(_settings.Mt32MasterVolume);
                Log($"SPE3 엔진 초기화 완료 ({_mt32.RomSet}) - {Mt32Engine.GetLastNativeMessage()}");
                Log("  └ 참고: 실기는 기본 상태에서 MIDI 채널 1번(0-based 0)에 파트가 " +
                    "배정되어 있지 않습니다. 채널 2번 이상으로 테스트해보세요.");
            }
            catch (DllNotFoundException)
            {
                Log("SPE3 초기화 실패: mt32_wrap.dll을 찾을 수 없습니다. " +
                    @"직접 빌드한 DLL을 exe와 같은 폴더(또는 Native\x64\)에 넣어주세요.");
            }
            catch (Exception ex)
            {
                Log($"SPE3 초기화 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// 폴더에서 ROM 파일을 찾습니다. 세 단계로 시도합니다.
        ///  1) 폴더 바로 아래에 표준 이름(MT32_CONTROL.ROM 등)이 있는지
        ///  2) 폴더 바로 아래에 키워드(CONTROL/PCM)가 들어간 파일이 있는지
        ///     - 덤프 파일명이 "MT32_CONTROL_1987-10-07_v1_07.ROM"처럼 길 수 있으므로
        ///  3) 하위 폴더까지 뒤져서 키워드 매칭
        ///     - 사용자가 MT32-old의 상위 폴더(Roms)를 골랐을 수도 있으므로
        /// </summary>
        private static string? FindRom(string folder, string exactStem, string keyword)
        {
            try
            {
                string exact = Path.Combine(folder, exactStem + ".ROM");
                if (File.Exists(exact)) return exact;

                var top = Directory.EnumerateFiles(folder)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase));
                if (top is not null) return top;

                return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(f).Contains(keyword, StringComparison.OrdinalIgnoreCase));
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// SC-55mkII ROM 5개의 실제 크기를 표준값과 대조해서 로그에 남깁니다.
        /// 표준: rom1=32KB, rom2=512KB, rom_sm=4KB, waverom1=2MB, waverom2=1MB
        /// </summary>
        private void LogSc55RomSizes(string folder)
        {
            var expected = new (string Name, long Size)[]
            {
                ("rom1.bin", 32768),
                ("rom2.bin", 524288),
                ("rom_sm.bin", 4096),
                ("waverom1.bin", 2097152),
                ("waverom2.bin", 1048576),
            };

            foreach (var (name, size) in expected)
            {
                string path = Path.Combine(folder, name);
                if (!File.Exists(path))
                {
                    Log($"  └ {name}: 없음 (!)");
                    continue;
                }
                long actual = new FileInfo(path).Length;
                string mark = actual == size ? "OK" : $"크기 불일치! 표준 {size:N0}바이트";
                Log($"  └ {name}: {actual:N0}바이트 - {mark}");
            }
        }

        /// <summary>
        /// ROM을 못 찾았을 때, 그 폴더에 실제로 어떤 파일이 있는지 로그로 보여줍니다.
        /// 경로는 맞는데 파일이 없는 건지, 엉뚱한 폴더를 고른 건지 바로 알 수 있습니다.
        /// </summary>
        private void LogFolderContents(string folder)
        {
            try
            {
                var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                    .Take(20)
                    .Select(f => Path.GetRelativePath(folder, f))
                    .ToList();

                if (files.Count == 0)
                {
                    Log($"  └ 그 폴더는 비어 있습니다: {folder}");
                    return;
                }

                Log($"  └ 그 폴더에 있는 파일: {string.Join(", ", files)}");
            }
            catch (Exception ex)
            {
                Log($"  └ 폴더를 읽지 못했습니다: {ex.Message}");
            }
        }

        private void StartSc55()
        {
            try
            {
                string folder = Sc55RomPathBox.Text;
                if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                {
                    Log("SPE5 건너뜀: SC-55mkII ROM 폴더가 지정되지 않았습니다 (SPE5 탭에서 폴더를 고르세요)");
                    return;
                }

                // 시작 전에 파일 5개의 크기를 찍어둡니다. 크기가 0이거나 표준과
                // 다르면 그 자체로 원인이 드러납니다(덤프 손상, 다른 기종 등).
                LogSc55RomSizes(folder);
                Log("  └ SPE5 펌웨어 부팅 중... (몇 초 걸립니다)");

                _sc55.RomDirectory = folder;
                _sc55.Open();
                Log($"SPE5 엔진 초기화 완료 - {Sc55Engine.GetLastNativeMessage()}");
                if (_settings.Sc55LcdContrast is >= 1 and <= 16)
                {
                    _sc55.SetLcdContrast(_settings.Sc55LcdContrast); // 지난번에 맞춰 둔 LCD 대비 복원
                    Log($"  └ SPE5 LCD 대비 {_settings.Sc55LcdContrast} 복원");
                }
            }
            catch (DllNotFoundException)
            {
                Log("SPE5 초기화 실패: nuked_sc55_wrap.dll을 찾을 수 없습니다. " +
                    "빌드한 DLL을 exe와 같은 폴더(또는 Native\\x64\\)에 넣어주세요.");
            }
            catch (Exception ex)
            {
                Log($"SPE5 초기화 실패: {ex.Message}");
                LogFolderContents(Sc55RomPathBox.Text);
            }
        }

        private void StartAudio()
        {
            try
            {
                if (_audioOutput is null)
                {
                    // 현재 기본 재생 장치 이름을 로그에 남긴다. 신호는 오는데 소리만 안 나는 증상(다른 프로그램이
                    // 먼저 그 장치를 한 번 연 뒤에야 정상화되는 경우가 있었다 - 원인은 Windows/드라이버 쪽으로 보이며
                    // 이 앱 안에서 재현/확정은 못 했다)이 다시 생기면, 이 로그로 어느 장치였는지 비교할 수 있다.
                    try
                    {
                        using var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                        using var device = enumerator.GetDefaultAudioEndpoint(
                            NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.Role.Multimedia);
                        Log($"  └ 기본 재생 장치: {device.FriendlyName}");
                    }
                    catch (Exception devEx)
                    {
                        Log($"  └ 기본 재생 장치를 확인하지 못했습니다: {devEx.Message}");
                    }

                    _audioOutput = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 30);
                    _audioOutput.Init(_mixer);
                }
                _audioOutput.Play();
                ApplyVolumes();
                Log("오디오 출력 시작");
            }
            catch (Exception ex)
            {
                Log($"오디오 출력 시작 실패: {ex.Message}");
            }
        }

        /// <summary>
        /// "오디오 출력 다시 시작" 버튼. WasapiOut을 완전히 버리고 새로 연다(장치를 다시 새로 잡는다).
        /// 신호(게이지)는 오는데 소리만 안 나는 증상이 있을 때, 앱을 통째로 껐다 켜지 않고
        /// 이 버튼만으로 같은 효과(오디오 장치를 새로 여는 것)를 내기 위한 것이다.
        /// MIDI 라우팅/엔진 상태는 그대로 두고 오디오 출력만 다시 만드므로 소리가 끊기지 않는다.
        /// </summary>
        private void OnRestartAudio(object sender, RoutedEventArgs e)
        {
            try
            {
                _audioOutput?.Stop();
                _audioOutput?.Dispose();
            }
            catch (Exception ex)
            {
                Log($"기존 오디오 출력 정리 중 오류(무시하고 계속): {ex.Message}");
            }
            _audioOutput = null;
            StartAudio();
        }

        private void StartMidiInput()
        {
            try
            {
                if (MidiInCombo.SelectedIndex < 0)
                {
                    Log("가상 입력 포트가 선택되지 않았습니다. loopMIDI 등을 설치 후 다시 실행하세요.");
                    return;
                }

                _midiIn?.Dispose(); // 재시작 시 이전 연결 정리
                _midiIn = new RawMidiIn();
                _midiIn.ShortMessageReceived += (_, m) => _router.HandleShortMessage(m.status, m.data1, m.data2);
                _midiIn.SysExReceived += (_, data) => _router.HandleSysEx(data);
                _midiIn.Open(MidiInCombo.SelectedIndex);
                Log("가상 MIDI 입력 연결 완료 - 대기 중 (SysEx 포함)");
            }
            catch (Exception ex)
            {
                Log($"MIDI 입력 연결 실패: {ex.Message}");
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            // 설정 저장 (실패해도 종료는 진행)
            SaveWindowSize();
            _settings.Mt32MasterVolume = Mt32Panel.MasterVolume;
            int contrast = _sc55.GetLcdContrast();
            if (contrast >= 1) _settings.Sc55LcdContrast = contrast; // 펌웨어 메뉴에서 맞춘 LCD 대비 기억
            CaptureUiToSettings();
            _settings.Save();

            // 열어둔 네이티브 자원(MIDI 핸들, 오디오 출력, 엔진 컨텍스트)을 정리합니다.
            _displayPollTimer.Stop();
            _audioOutput?.Stop();
            _audioOutput?.Dispose();
            _midiIn?.Dispose();
            _mt32.Dispose();
            _sc55.Dispose();
            base.OnClosed(e);
        }

        private void Log(string message) => LogList.Items.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {Loc.T(message)}");
    }
}
