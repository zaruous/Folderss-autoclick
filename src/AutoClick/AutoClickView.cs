using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Folderss.Plugins;

namespace AutoClick
{
    /// <summary>
    /// AutoClick 화면. 원본 AutoClick 2.1의 구성(클릭·횟수·단축키·간격·옵션·위치 기록)을 옮기고 예약(시작 시각·종료 시각)을 더했다.
    /// 전역 단축키는 이 창 핸들에 등록하므로 창이 열려 있는 동안만 동작하고, 창을 닫으면 클릭도(예약 대기도) 멈춘다.
    /// </summary>
    internal sealed class AutoClickView : UserControl
    {
        private const int StartHotkeyId = 1;
        private const int RecordHotkeyId = 2;

        /// <summary>버튼으로 시작하면 포인터가 [시작] 버튼 위에 있으므로, 클릭할 곳으로 옮길 시간을 준다.</summary>
        private const int StartDelaySeconds = 3;

        private readonly IPluginManager _manager;
        private readonly AutoClickSettings _settings;
        private readonly DispatcherTimer _progressTimer;
        private readonly DispatcherTimer _countdownTimer;
        private readonly DispatcherTimer _clockTimer;

        private ComboBox _button;
        private ComboBox _clickType;
        private CheckBox _counterEnabled;
        private TextBox _counterLimit;
        private ComboBox _startModifier;
        private ComboBox _startKey;
        private ComboBox _recordModifier;
        private ComboBox _recordKey;
        private TextBox _hours;
        private TextBox _minutes;
        private TextBox _seconds;
        private TextBox _tenths;
        private TextBox _hundredths;
        private TextBox _thousandths;
        private TextBlock _intervalTotal;
        private ScheduleRow _scheduleStart;
        private ScheduleRow _scheduleUntil;
        private CheckBox _freezePointer;
        private CheckBox _keepOnTop;
        private CheckBox _hideWhileClicking;
        private CheckBox _showOnStop;
        private CheckBox _smartClick;
        private TextBlock _pointsTitle;
        private ListBox _points;
        private FrameworkElement _options;
        private Button _startButton;
        private TextBlock _status;
        private TextBlock _clickCount;

        private Window _window;
        private bool _closed;
        private HotkeyHost _hotkeys;
        private ClickRunner _runner;
        private int _lastClicks;
        private int _countdown;
        private bool _hidePending;
        private bool _hiddenByRun;
        private string _runNote;

        public AutoClickView(IPluginManager manager)
        {
            _manager = manager;
            _settings = AutoClickSettings.Load(manager);

            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            _progressTimer.Tick += (s, e) => ShowProgress();
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += CountdownTimer_Tick;
            _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _clockTimer.Tick += (s, e) => ShowScheduleHints();

            Content = BuildContent();
            RefreshPoints();
            ShowIntervalTotal();
            ShowScheduleHints();
            ShowClickCount();
            UpdateStartButton();
            SetStatus(IdleMessage());

            Loaded += View_Loaded;
        }

        /// <summary>창이 닫혀 클릭·단축키를 모두 정리하고 설정을 저장한 뒤.</summary>
        public event EventHandler Closed;

        /// <summary>창에 붙어 있고 아직 닫히지 않았는지.</summary>
        public bool IsOpen { get { return _window != null && !_closed; } }

        /// <summary>창을 앞으로 가져온다. 클릭 중이라 숨긴 창이면 다시 보인다.</summary>
        public void BringToFront()
        {
            if (!IsOpen)
                return;
            if (!_window.IsVisible)
                _window.Show();
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            _window.Activate();
        }

        // ── 화면 구성 ──────────────────────────────────────────────

        private UIElement BuildContent()
        {
            _button = Combo(new[] { "왼쪽", "오른쪽", "가운데" }, (int)_settings.Button);
            _clickType = Combo(new[] { "싱글 클릭", "더블 클릭" }, _settings.DoubleClick ? 1 : 0);
            var click = Section("클릭", Stack(Labeled("마우스", 48, _button), Labeled("방식", 48, _clickType)));

            _counterEnabled = Check("횟수 제한", _settings.CounterEnabled);
            _counterLimit = NumberBox(_settings.CounterLimit, 999999, 72);
            var counter = Section("횟수", Stack(_counterEnabled, Row(_counterLimit, Hint("회 후 멈춤"))));

            _startModifier = ModifierCombo(_settings.StartHotkey);
            _startKey = KeyCombo(_settings.StartHotkey);
            _recordModifier = ModifierCombo(_settings.RecordHotkey);
            _recordKey = KeyCombo(_settings.RecordHotkey);
            foreach (var combo in new[] { _startModifier, _startKey, _recordModifier, _recordKey })
                combo.SelectionChanged += (s, e) => RegisterHotkeys();
            var hotkeys = Section("단축키 (어느 창에서나)", Stack(
                Labeled("시작/중지", 64, Row(_startModifier, Hint("+"), _startKey)),
                Labeled("위치 기록", 64, Row(_recordModifier, Hint("+"), _recordKey))));

            var ms = _settings.IntervalMs;
            _hours = NumberBox(ms / 3600000, 99, 44);
            _minutes = NumberBox(ms / 60000 % 60, 59, 44);
            _seconds = NumberBox(ms / 1000 % 60, 59, 44);
            _tenths = NumberBox(ms / 100 % 10, 9, 36);
            _hundredths = NumberBox(ms / 10 % 10, 9, 36);
            _thousandths = NumberBox(ms % 10, 9, 36);
            _intervalTotal = Hint("");
            foreach (var box in new[] { _hours, _minutes, _seconds, _tenths, _hundredths, _thousandths })
                box.TextChanged += (s, e) => ShowIntervalTotal();
            var interval = Section("클릭 간격", Row(
                Unit(_hours, "시간"), Unit(_minutes, "분"), Unit(_seconds, "초"),
                Unit(_tenths, "1/10초"), Unit(_hundredths, "1/100초"), Unit(_thousandths, "1/1000초"),
                _intervalTotal));

            _scheduleStart = new ScheduleRow("예약 시작", _settings.ScheduleStartEnabled, _settings.ScheduleStartTime);
            _scheduleUntil = new ScheduleRow("종료 시각 (until)", _settings.ScheduleUntilEnabled, _settings.ScheduleUntilTime);
            foreach (var row in new[] { _scheduleStart, _scheduleUntil })
                row.Changed += (s, e) => ScheduleChanged();
            var schedule = Section("예약 (날짜는 오늘이 기본)", Stack(_scheduleStart.View, _scheduleUntil.View));

            _freezePointer = Check("포인터 고정 (시작한 위치에서만 클릭)", _settings.FreezePointer);
            _keepOnTop = Check("항상 위", _settings.KeepOnTop);
            _keepOnTop.Click += (s, e) =>
            {
                if (IsOpen)
                    _window.Topmost = _keepOnTop.IsChecked == true;
            };
            _hideWhileClicking = Check("클릭하는 동안 창 숨기기", _settings.HideWhileClicking);
            _showOnStop = Check("멈추면 창을 앞으로", _settings.ShowOnStop);
            var options = Section("옵션", Stack(_freezePointer, _keepOnTop, _hideWhileClicking, _showOnStop));

            _smartClick = Check("기록한 위치를 차례로 클릭 (Smart Click)", _settings.SmartClick);
            _smartClick.Click += (s, e) => UpdateFreezePointerEnabled();
            UpdateFreezePointerEnabled();
            _pointsTitle = Hint("");
            _pointsTitle.Margin = new Thickness(0, 4, 0, 4);
            _points = new ListBox { Height = 88, SelectionMode = SelectionMode.Extended };
            _points.Loaded += (s, e) => ThemeListBox(_points);
            var buttons = Row(Btn("선택 삭제", RemoveSelectedPoints), Btn("모두 지우기", ClearPoints));
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            var smartPanel = new DockPanel();
            DockPanel.SetDock(_smartClick, Dock.Top);
            DockPanel.SetDock(_pointsTitle, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            smartPanel.Children.Add(_smartClick);
            smartPanel.Children.Add(_pointsTitle);
            smartPanel.Children.Add(buttons);
            smartPanel.Children.Add(_points);
            var smart = Section("위치 기록", smartPanel);

            var settingsArea = new StackPanel();
            settingsArea.Children.Add(Columns(new[] { 1.0, 1.0, 1.35 }, click, counter, hotkeys));
            settingsArea.Children.Add(interval);
            settingsArea.Children.Add(schedule);
            settingsArea.Children.Add(Columns(new[] { 1.0, 1.5 }, options, smart));
            _options = settingsArea;

            _startButton = new Button { MinWidth = 140, FontWeight = FontWeights.SemiBold, Padding = new Thickness(16, 8, 16, 8) };
            _startButton.Click += (s, e) => Toggle(true);
            _clickCount = Hint("");
            _clickCount.Margin = new Thickness(8, 0, 4, 0);
            _status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(8, 0, 0, 0)
            };
            var bar = new DockPanel { Margin = new Thickness(1, 6, 1, 0) };
            DockPanel.SetDock(_startButton, Dock.Left);
            DockPanel.SetDock(_clickCount, Dock.Right);
            bar.Children.Add(_startButton);
            bar.Children.Add(_clickCount);
            bar.Children.Add(_status);

            var root = new DockPanel { Width = 680, Margin = new Thickness(12) };
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(settingsArea);
            return root;
        }

        /// <summary>
        /// WPF 기본 ListBox 템플릿은 잠기면(실행 중 설정 영역을 막을 때) 바탕을 흰색으로 바꾼다.
        /// Folderss 테마는 색만 정하고 템플릿은 그대로 쓰므로, 바탕·테두리를 테마 색에 고정한다.
        /// </summary>
        private static void ThemeListBox(ListBox list)
        {
            var border = list.Template.FindName("Bd", list) as Border;
            if (border == null)
                return;
            border.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
        }

        private static Border Section(string title, UIElement content)
        {
            var header = Hint(title);
            header.FontWeight = FontWeights.SemiBold;
            header.Margin = new Thickness(0, 0, 0, 6);
            var panel = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            panel.Children.Add(header);
            panel.Children.Add(content);

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(3),
                Child = panel
            };
            border.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");
            return border;
        }

        private static Grid Columns(double[] weights, params UIElement[] children)
        {
            var grid = new Grid();
            for (var i = 0; i < children.Length; i++)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weights[i], GridUnitType.Star) });
                Grid.SetColumn(children[i], i);
                grid.Children.Add(children[i]);
            }
            return grid;
        }

        private static StackPanel Stack(params UIElement[] children)
        {
            var panel = new StackPanel();
            foreach (var child in children)
                panel.Children.Add(child);
            return panel;
        }

        private static StackPanel Row(params UIElement[] children)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var child in children)
                panel.Children.Add(child);
            return panel;
        }

        private static DockPanel Labeled(string label, double labelWidth, UIElement control)
        {
            var text = new TextBlock { Text = label, Width = labelWidth, VerticalAlignment = VerticalAlignment.Center };
            var panel = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            DockPanel.SetDock(text, Dock.Left);
            panel.Children.Add(text);
            panel.Children.Add(control);
            return panel;
        }

        private static StackPanel Unit(TextBox box, string unit)
        {
            var text = Hint(unit);
            text.Margin = new Thickness(4, 0, 12, 0);
            return Row(box, text);
        }

        private static TextBlock Hint(string text)
        {
            var block = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            block.SetResourceReference(TextBlock.ForegroundProperty, "SecondaryText");
            return block;
        }

        private static CheckBox Check(string text, bool value)
        {
            return new CheckBox { Content = text, IsChecked = value, Margin = new Thickness(0, 3, 0, 3), VerticalAlignment = VerticalAlignment.Center };
        }

        private static Button Btn(string text, Action action)
        {
            var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4) };
            button.Click += (s, e) => action();
            return button;
        }

        private static ComboBox Combo(string[] items, int selectedIndex)
        {
            return new ComboBox { ItemsSource = items, SelectedIndex = Math.Clamp(selectedIndex, 0, items.Length - 1), MinWidth = 96 };
        }

        private static ComboBox ModifierCombo(Hotkey hotkey)
        {
            var names = Hotkey.ModifierOptions.Select(o => o.Key).ToArray();
            var index = Hotkey.ModifierOptions.Select(o => o.Value).ToList().IndexOf(hotkey.Modifiers);
            var combo = Combo(names, Math.Max(0, index));
            combo.MinWidth = 92;
            return combo;
        }

        private static ComboBox KeyCombo(Hotkey hotkey)
        {
            var combo = Combo(Enumerable.Range(1, 12).Select(n => "F" + n).ToArray(), hotkey.FunctionKey - 1);
            combo.MinWidth = 64;
            return combo;
        }

        private static Hotkey SelectedHotkey(ComboBox modifier, ComboBox key)
        {
            return new Hotkey(Hotkey.ModifierOptions[Math.Max(0, modifier.SelectedIndex)].Value, key.SelectedIndex + 1);
        }

        /// <summary>숫자만 받는 입력란. ↑↓ 키와 마우스 휠로 1씩 바꾼다. 범위를 벗어난 값은 읽을 때 max로 자른다.</summary>
        private static TextBox NumberBox(int value, int max, double width)
        {
            var box = new TextBox
            {
                Width = width,
                Padding = new Thickness(4, 3, 4, 3),
                HorizontalContentAlignment = HorizontalAlignment.Right,
                MaxLength = max.ToString(CultureInfo.InvariantCulture).Length,
                Tag = max,
                Text = Math.Clamp(value, 0, max).ToString(CultureInfo.InvariantCulture),
                ToolTip = "↑↓ 키나 마우스 휠로 바꿀 수 있습니다"
            };
            box.PreviewTextInput += (s, e) => e.Handled = !e.Text.All(char.IsDigit);
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Up || e.Key == Key.Down)
                {
                    StepNumber(box, e.Key == Key.Up ? 1 : -1);
                    e.Handled = true;
                }
                else if (e.Key == Key.Space)
                {
                    e.Handled = true;
                }
            };
            box.PreviewMouseWheel += (s, e) =>
            {
                StepNumber(box, e.Delta > 0 ? 1 : -1);
                e.Handled = true;
            };
            box.LostFocus += (s, e) => box.Text = ReadNumber(box).ToString(CultureInfo.InvariantCulture);
            return box;
        }

        private static void StepNumber(TextBox box, int delta)
        {
            box.Text = Math.Clamp(ReadNumber(box) + delta, 0, (int)box.Tag).ToString(CultureInfo.InvariantCulture);
            box.CaretIndex = box.Text.Length;
        }

        private static int ReadNumber(TextBox box)
        {
            int value;
            return int.TryParse(box.Text, NumberStyles.None, CultureInfo.InvariantCulture, out value) ? Math.Min(value, (int)box.Tag) : 0;
        }

        /// <summary>
        /// 예약 한 줄: [사용] 날짜 시 분 초 (남은 시간). 날짜는 창을 열 때마다 오늘로 두고, 시·분·초는 저장한 값을 쓴다.
        /// 끄면 날짜·시각 입력을 막는다.
        /// </summary>
        private sealed class ScheduleRow
        {
            private readonly CheckBox _enabled;
            private readonly DatePicker _date;
            private readonly TextBox _hour;
            private readonly TextBox _minute;
            private readonly TextBox _second;
            private readonly FrameworkElement _inputs;

            public ScheduleRow(string label, bool enabled, TimeSpan time)
            {
                _enabled = Check(label, enabled);
                _enabled.Width = 128;
                _date = new DatePicker
                {
                    SelectedDate = DateTime.Today,
                    Width = 120,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    ToolTip = "기본은 오늘입니다"
                };
                _date.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
                _date.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
                _date.SetResourceReference(Control.BorderBrushProperty, "BorderBrush");
                _date.Loaded += (s, e) => ThemeDatePicker(_date);
                _hour = NumberBox(time.Hours, 23, 40);
                _minute = NumberBox(time.Minutes, 59, 40);
                _second = NumberBox(time.Seconds, 59, 40);
                HintText = Hint("");
                _inputs = Row(_date, Unit(_hour, "시"), Unit(_minute, "분"), Unit(_second, "초"));
                View = Row(_enabled, _inputs, HintText);
                View.Margin = new Thickness(0, 1, 0, 1);

                _enabled.Click += (s, e) => OnChanged();
                _date.SelectedDateChanged += (s, e) => OnChanged();
                foreach (var box in new[] { _hour, _minute, _second })
                    box.TextChanged += (s, e) => OnChanged();
                _inputs.IsEnabled = enabled;
            }

            public event EventHandler Changed;

            public StackPanel View { get; }

            /// <summary>오른쪽의 남은 시간 안내.</summary>
            public TextBlock HintText { get; }

            public bool IsEnabled { get { return _enabled.IsChecked == true; } }

            public TimeSpan TimeOfDay { get { return new TimeSpan(ReadNumber(_hour), ReadNumber(_minute), ReadNumber(_second)); } }

            /// <summary>고른 날짜 + 시각. 날짜를 지웠으면 null.</summary>
            public DateTime? At { get { return _date.SelectedDate.HasValue ? _date.SelectedDate.Value.Date + TimeOfDay : (DateTime?)null; } }

            /// <summary>
            /// Folderss 테마에는 DatePicker 스타일이 없어서 어두운 테마에서도 안쪽 입력란이 흰 바탕·흰 테두리로 나온다.
            /// 안쪽 입력란을 테마 색에 연결한다. 달력 팝업은 기본 모양 그대로 둔다.
            /// </summary>
            private static void ThemeDatePicker(DatePicker picker)
            {
                var box = picker.Template.FindName("PART_TextBox", picker) as DatePickerTextBox;
                if (box == null)
                    return;
                box.SetResourceReference(Control.BackgroundProperty, "ControlBackground");
                box.SetResourceReference(Control.ForegroundProperty, "PrimaryText");
                box.SetResourceReference(TextBoxBase.CaretBrushProperty, "PrimaryText");
                box.BorderBrush = Brushes.Transparent;
                // 잠기면(실행 중) 반투명 흰 막을 덮는 부분. 어두운 테마에서 밝게 튀므로 쓰지 않는다
                var disabledVisual = picker.Template.FindName("PART_DisabledVisual", picker) as UIElement;
                if (disabledVisual != null)
                    disabledVisual.Visibility = Visibility.Collapsed;
                box.ApplyTemplate();
                // 기본 템플릿 안쪽의 흰 1px 테두리 두 개
                foreach (var name in new[] { "ContentElement", "watermark_decorator" })
                {
                    var border = box.Template.FindName(name, box) as Border;
                    if (border != null)
                        border.BorderBrush = Brushes.Transparent;
                }
            }

            private void OnChanged()
            {
                _inputs.IsEnabled = IsEnabled;
                var changed = Changed;
                if (changed != null)
                    changed(this, EventArgs.Empty);
            }
        }

        // ── 창·단축키 ──────────────────────────────────────────────

        private void View_Loaded(object sender, RoutedEventArgs e)
        {
            if (_window != null)
                return;
            _window = Window.GetWindow(this);
            if (_window == null)
                return;

            _window.Closed += Window_Closed;
            FitWindow(_window);
            _window.Topmost = _settings.KeepOnTop;
            _hotkeys = new HotkeyHost(HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle));
            RegisterHotkeys();
            _clockTimer.Start();
        }

        /// <summary>본체가 900×600으로 띄운 창을 내용 크기로 줄인다. 본체가 잡아 둔 가운데 위치는 유지한다.</summary>
        private static void FitWindow(Window window)
        {
            var centerX = window.Left + window.ActualWidth / 2;
            var centerY = window.Top + window.ActualHeight / 2;
            window.ResizeMode = ResizeMode.CanMinimize;
            window.SizeToContent = SizeToContent.WidthAndHeight;
            window.UpdateLayout();
            window.Left = centerX - window.ActualWidth / 2;
            window.Top = centerY - window.ActualHeight / 2;
        }

        private void RegisterHotkeys()
        {
            if (_hotkeys == null)
                return;

            var start = SelectedHotkey(_startModifier, _startKey);
            var record = SelectedHotkey(_recordModifier, _recordKey);
            _hotkeys.Unregister(StartHotkeyId);
            _hotkeys.Unregister(RecordHotkeyId);

            string problem = null;
            if (start.Equals(record))
            {
                problem = "시작/중지와 위치 기록 단축키가 같습니다. 서로 다르게 정하세요.";
            }
            else
            {
                var error = _hotkeys.Register(StartHotkeyId, start, () => Toggle(false));
                if (error != null)
                    problem = "시작/중지 단축키(" + start + ")를 등록하지 못했습니다: " + error;
                error = _hotkeys.Register(RecordHotkeyId, record, RecordPoint);
                if (error != null)
                    problem = (problem == null ? "" : problem + " / ") + "위치 기록 단축키(" + record + ")를 등록하지 못했습니다: " + error;
            }

            UpdateStartButton();
            if (IsIdle)
                SetStatus(problem ?? IdleMessage());
        }

        private void Window_Closed(object sender, EventArgs e)
        {
            _closed = true;
            _countdownTimer.Stop();
            _progressTimer.Stop();
            _clockTimer.Stop();
            if (_runner != null)
                _runner.Stop();
            if (_hotkeys != null)
            {
                _hotkeys.Dispose();
                _hotkeys = null;
            }

            try
            {
                CollectSettings();
                _settings.Save(_manager);
            }
            catch (Exception ex)
            {
                MessageBox.Show("AutoClick 설정을 저장하지 못했습니다.\n" + ex.Message, "AutoClick", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            var closed = Closed;
            if (closed != null)
                closed(this, EventArgs.Empty);
        }

        // ── 시작·중지 ──────────────────────────────────────────────

        private bool IsIdle { get { return _runner == null && !_countdownTimer.IsEnabled; } }

        /// <summary>
        /// 시작/중지 단축키와 [시작] 버튼. 카운트다운 중이면 취소, 클릭 중이거나 예약 대기 중이면 멈춘다.
        /// 예약 시작을 켜 두었으면 버튼으로 눌러도 카운트다운 없이 바로 예약한다(예약 시각까지 포인터를 옮길 시간이 있다).
        /// </summary>
        private void Toggle(bool fromButton)
        {
            if (_countdownTimer.IsEnabled)
            {
                _countdownTimer.Stop();
                _options.IsEnabled = true;
                UpdateStartButton();
                SetStatus("시작을 취소했습니다.");
            }
            else if (_runner != null)
            {
                _runner.Stop();
                SetStatus(_runner.HasStarted ? "멈추는 중…" : "예약을 취소하는 중…");
            }
            else if (fromButton && !_scheduleStart.IsEnabled)
            {
                CollectSettings();
                DateTime? startAt, stopAt;
                var problem = ValidateStart(out startAt, out stopAt);
                if (problem != null)
                {
                    SetStatus(problem);
                    return;
                }
                _countdown = StartDelaySeconds;
                _options.IsEnabled = false;
                _countdownTimer.Start();
                ShowCountdown();
            }
            else
            {
                StartClicking();
            }
        }

        private void CountdownTimer_Tick(object sender, EventArgs e)
        {
            _countdown--;
            if (_countdown > 0)
            {
                ShowCountdown();
                return;
            }
            _countdownTimer.Stop();
            StartClicking();
        }

        private void ShowCountdown()
        {
            UpdateStartButton();
            SetStatus(_countdown + "초 뒤에 시작합니다. 클릭할 곳으로 마우스를 옮기세요.");
        }

        /// <summary>시작할 수 있는지 확인하고, 예약 시작·종료 시각을 정한다. 문제가 있으면 그 메시지.</summary>
        private string ValidateStart(out DateTime? startAt, out DateTime? stopAt)
        {
            startAt = null;
            stopAt = null;
            if (_settings.IntervalMs < 1)
                return "클릭 간격을 1/1000초 이상으로 정하세요.";
            if (_settings.SmartClick && _settings.Points.Count == 0)
                return "기록한 위치가 없습니다. 클릭할 곳에 마우스를 두고 " + _settings.RecordHotkey + " 키로 기록하세요.";

            var now = DateTime.Now;
            if (_scheduleStart.IsEnabled)
            {
                startAt = _scheduleStart.At;
                if (!startAt.HasValue)
                    return "예약 시작 날짜를 고르세요.";
                if (startAt.Value <= now)
                    return "예약 시작 시각(" + Stamp(startAt.Value) + ")이 이미 지났습니다.";
            }
            if (_scheduleUntil.IsEnabled)
            {
                stopAt = _scheduleUntil.At;
                if (!stopAt.HasValue)
                    return "종료 날짜를 고르세요.";
                if (startAt.HasValue && stopAt.Value <= startAt.Value)
                    return "종료 시각(" + Stamp(stopAt.Value) + ")이 예약 시작 시각(" + Stamp(startAt.Value) + ")보다 뒤여야 합니다.";
                if (stopAt.Value <= now)
                    return "종료 시각(" + Stamp(stopAt.Value) + ")이 이미 지났습니다.";
            }
            return null;
        }

        private void StartClicking()
        {
            CollectSettings();
            DateTime? startAt, stopAt;
            var problem = ValidateStart(out startAt, out stopAt);
            if (problem == null)
            {
                try
                {
                    var job = new ClickJob
                    {
                        Button = _settings.Button,
                        DoubleClick = _settings.DoubleClick,
                        IntervalMs = _settings.IntervalMs,
                        MaxClicks = _settings.CounterEnabled ? _settings.CounterLimit : 0,
                        FreezePointer = !_settings.SmartClick && _settings.FreezePointer,
                        StartAt = startAt,
                        StopAt = stopAt
                    };
                    if (_settings.SmartClick)
                        job.Points = _settings.Points.ToArray();

                    _runner = new ClickRunner(job, Dispatcher, Runner_Finished);
                    _runner.Start();
                }
                catch (Exception ex)
                {
                    _runner = null;
                    problem = "시작하지 못했습니다: " + ex.Message;
                }
            }

            if (problem != null)
            {
                _options.IsEnabled = true;
                UpdateStartButton();
                SetStatus(problem);
                return;
            }

            _lastClicks = 0;
            _runNote = null;
            _options.IsEnabled = false;
            if (_settings.HideWhileClicking && IsOpen && _window.IsVisible)
            {
                // 단축키가 없으면 숨긴 창을 다시 불러올 방법이 없으므로 숨기지 않는다
                if (CanStopByHotkey)
                    _hidePending = true;
                else
                    _runNote = " (단축키가 없어 창을 숨기지 않습니다)";
            }
            ShowProgress();
            _progressTimer.Start();
        }

        private bool CanStopByHotkey { get { return _hotkeys != null && _hotkeys.IsRegistered(StartHotkeyId); } }

        /// <summary>실행 중 0.1초마다: 클릭 수, 버튼, 상태 줄. 예약 대기가 끝나 클릭을 시작하면 그때 창을 숨긴다.</summary>
        private void ShowProgress()
        {
            if (_runner == null)
                return;
            var job = _runner.Job;
            var clicking = _runner.HasStarted || !job.StartAt.HasValue;
            if (_hidePending && clicking && IsOpen)
            {
                _hidePending = false;
                _hiddenByRun = true;
                _window.Hide();
            }

            ShowClickCount();
            UpdateStartButton();

            var hotkey = CanStopByHotkey ? _settings.StartHotkey + " 키나 " : "";
            string text;
            if (!clicking)
            {
                // 취소 방법은 버튼("예약 취소 (F3)")에 나오므로 상태 줄에는 쓰지 않는다(창 너비에 맞춤)
                text = "예약 대기 — " + Stamp(job.StartAt.Value) + "에 시작 (" + FormatDuration(job.StartAt.Value - DateTime.Now) + " 남음)"
                    + (job.StopAt.HasValue ? " · " + Stamp(job.StopAt.Value) + "까지" : "");
            }
            else if (job.StopAt.HasValue)
            {
                text = "클릭 중 — " + Stamp(job.StopAt.Value) + "까지 (" + FormatDuration(job.StopAt.Value - DateTime.Now) + " 남음)"
                    + " · " + hotkey + "[중지]로 멈춤";
            }
            else
            {
                text = "클릭 중 — " + hotkey + "[중지]로 멈춥니다.";
            }
            SetStatus(text + _runNote);
        }

        private void Runner_Finished(ClickRunner runner, string error)
        {
            if (runner != _runner)
                return;
            _runner = null;
            _hidePending = false;
            _lastClicks = runner.Clicks;
            _progressTimer.Stop();
            ShowClickCount();
            _options.IsEnabled = true;
            UpdateStartButton();

            var clicks = runner.Clicks.ToString("N0", CultureInfo.CurrentCulture);
            if (error != null)
                SetStatus("오류로 멈췄습니다: " + error);
            else if (!runner.HasStarted)
                SetStatus("예약을 취소했습니다.");
            else if (runner.ReachedStopTime)
                SetStatus("종료 시각(" + Stamp(runner.Job.StopAt.Value) + ")이 되어 멈췄습니다. " + clicks + "회 클릭했습니다.");
            else if (runner.ReachedLimit)
                SetStatus(clicks + "회 클릭하고 멈췄습니다.");
            else
                SetStatus("멈췄습니다. " + IdleMessage());

            if (!IsOpen)
                return;
            if (_hiddenByRun)
            {
                _hiddenByRun = false;
                _window.Show();
            }
            if (_settings.ShowOnStop)
                BringToFront();
        }

        // ── 위치 기록 ──────────────────────────────────────────────

        private void RecordPoint()
        {
            try
            {
                var point = NativeMethods.GetCursorPosition();
                _settings.Points.Add(point);
                RefreshPoints();
                _points.ScrollIntoView(_points.Items[_points.Items.Count - 1]);
                SetStatus("위치를 기록했습니다: " + point + " — 모두 " + _settings.Points.Count + "개");
            }
            catch (Exception ex)
            {
                SetStatus("위치를 기록하지 못했습니다: " + ex.Message);
            }
        }

        private void RemoveSelectedPoints()
        {
            var indexes = _points.SelectedItems.Cast<object>().Select(item => _points.Items.IndexOf(item)).OrderByDescending(i => i).ToList();
            foreach (var index in indexes)
                _settings.Points.RemoveAt(index);
            RefreshPoints();
        }

        private void ClearPoints()
        {
            _settings.Points.Clear();
            RefreshPoints();
        }

        private void RefreshPoints()
        {
            _points.Items.Clear();
            for (var i = 0; i < _settings.Points.Count; i++)
                _points.Items.Add((i + 1) + ".  " + _settings.Points[i]);
            _pointsTitle.Text = _settings.Points.Count == 0
                ? "기록한 위치가 없습니다. 클릭할 곳에 마우스를 두고 위치 기록 단축키를 누르세요."
                : "기록한 위치 " + _settings.Points.Count + "개 (위에서부터 차례로 클릭)";
        }

        // ── 화면 값 ──────────────────────────────────────────────

        private void CollectSettings()
        {
            _settings.Button = (MouseButtonKind)Math.Max(0, _button.SelectedIndex);
            _settings.DoubleClick = _clickType.SelectedIndex == 1;
            _settings.IntervalMs = ReadInterval();
            _settings.CounterEnabled = _counterEnabled.IsChecked == true;
            _settings.CounterLimit = Math.Max(1, ReadNumber(_counterLimit));
            _settings.FreezePointer = _freezePointer.IsChecked == true;
            _settings.KeepOnTop = _keepOnTop.IsChecked == true;
            _settings.HideWhileClicking = _hideWhileClicking.IsChecked == true;
            _settings.ShowOnStop = _showOnStop.IsChecked == true;
            _settings.SmartClick = _smartClick.IsChecked == true;
            _settings.StartHotkey = SelectedHotkey(_startModifier, _startKey);
            _settings.RecordHotkey = SelectedHotkey(_recordModifier, _recordKey);
            _settings.ScheduleStartEnabled = _scheduleStart.IsEnabled;
            _settings.ScheduleStartTime = _scheduleStart.TimeOfDay;
            _settings.ScheduleUntilEnabled = _scheduleUntil.IsEnabled;
            _settings.ScheduleUntilTime = _scheduleUntil.TimeOfDay;
        }

        private int ReadInterval()
        {
            return ReadNumber(_hours) * 3600000
                + ReadNumber(_minutes) * 60000
                + ReadNumber(_seconds) * 1000
                + ReadNumber(_tenths) * 100
                + ReadNumber(_hundredths) * 10
                + ReadNumber(_thousandths);
        }

        private void ShowIntervalTotal()
        {
            var ms = ReadInterval();
            if (ms < 1)
                _intervalTotal.Text = "간격이 0입니다";
            else if (ms < 60000)
                _intervalTotal.Text = "= " + (ms / 1000.0).ToString("0.000", CultureInfo.InvariantCulture) + "초";
            else
                _intervalTotal.Text = "= " + TimeSpan.FromMilliseconds(ms).ToString(@"h\:mm\:ss\.fff", CultureInfo.InvariantCulture);
        }

        private void ScheduleChanged()
        {
            ShowScheduleHints();
            UpdateStartButton();
            if (IsIdle)
                SetStatus(IdleMessage());
        }

        /// <summary>예약 줄 오른쪽에 지금부터 얼마 뒤인지 보인다. 1초마다 다시 계산한다.</summary>
        private void ShowScheduleHints()
        {
            var now = DateTime.Now;
            var startAt = _scheduleStart.At;
            _scheduleStart.HintText.Text = Describe(startAt, now, "뒤");

            var untilAt = _scheduleUntil.At;
            if (_scheduleStart.IsEnabled && startAt.HasValue && untilAt.HasValue)
                _scheduleUntil.HintText.Text = untilAt.Value <= startAt.Value
                    ? "시작 시각보다 뒤여야 합니다"
                    : "시작 후 " + FormatDuration(untilAt.Value - startAt.Value) + " 동안";
            else
                _scheduleUntil.HintText.Text = Describe(untilAt, now, "뒤까지");
        }

        private static string Describe(DateTime? at, DateTime now, string suffix)
        {
            if (!at.HasValue)
                return "날짜를 고르세요";
            return at.Value <= now ? "지난 시각입니다" : FormatDuration(at.Value - now) + " " + suffix;
        }

        /// <summary>"1일 2시간 3분", "2시간 3분 4초", "3분 4초", "4초". 초는 올림.</summary>
        private static string FormatDuration(TimeSpan span)
        {
            var total = (long)Math.Max(0, Math.Ceiling(span.TotalSeconds));
            long days = total / 86400, hours = total / 3600 % 24, minutes = total / 60 % 60, seconds = total % 60;
            if (days > 0)
                return days + "일 " + hours + "시간 " + minutes + "분";
            if (hours > 0)
                return hours + "시간 " + minutes + "분 " + seconds + "초";
            if (minutes > 0)
                return minutes + "분 " + seconds + "초";
            return seconds + "초";
        }

        /// <summary>오늘이면 시각만, 다른 날이면 날짜까지.</summary>
        private static string Stamp(DateTime at)
        {
            return at.ToString(at.Date == DateTime.Today ? "HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void UpdateFreezePointerEnabled()
        {
            // 위치 기록 클릭은 기록한 위치로 옮겨 다니므로 포인터 고정이 의미 없다
            _freezePointer.IsEnabled = _smartClick.IsChecked != true;
        }

        private void ShowClickCount()
        {
            var clicks = _runner != null ? _runner.Clicks : _lastClicks;
            _clickCount.Text = "클릭 " + clicks.ToString("N0", CultureInfo.CurrentCulture) + "회";
        }

        private void UpdateStartButton()
        {
            var hotkey = SelectedHotkey(_startModifier, _startKey);
            if (_countdownTimer.IsEnabled)
                _startButton.Content = "취소 (" + _countdown + ")";
            else if (_runner != null)
                _startButton.Content = (_runner.HasStarted || !_runner.Job.StartAt.HasValue ? "중지 (" : "예약 취소 (") + hotkey + ")";
            else
                _startButton.Content = (_scheduleStart.IsEnabled ? "예약 (" : "시작 (") + hotkey + ")";
        }

        private string IdleMessage()
        {
            return SelectedHotkey(_startModifier, _startKey) + ": 시작/중지 · "
                + SelectedHotkey(_recordModifier, _recordKey) + ": 위치 기록 · "
                + (_scheduleStart.IsEnabled
                    ? "[예약]을 누르면 예약 시작 시각까지 기다립니다."
                    : "[시작] 버튼은 " + StartDelaySeconds + "초 뒤에 시작합니다.");
        }

        private void SetStatus(string text)
        {
            _status.Text = text;
            _status.ToolTip = text;
        }
    }
}
