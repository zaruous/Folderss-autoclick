using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using Folderss.Plugins;

namespace AutoClick
{
    internal enum MouseButtonKind
    {
        Left,
        Right,
        Middle
    }

    /// <summary>화면 좌표(픽셀).</summary>
    internal readonly struct ScreenPoint
    {
        public ScreenPoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X { get; }
        public int Y { get; }

        public override string ToString()
        {
            return "(" + X.ToString(CultureInfo.InvariantCulture) + ", " + Y.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

    /// <summary>
    /// 화면의 모든 선택 값. 플러그인 설정(%LOCALAPPDATA%\Folderss\plugin-data\&lt;id&gt;\settings.json)에 키별 문자열로 저장한다.
    /// 읽지 못한 값은 기본값(원본 AutoClick 2.1과 같은 값)으로 둔다.
    /// </summary>
    internal sealed class AutoClickSettings
    {
        public static readonly Hotkey DefaultStartHotkey = new Hotkey(ModifierKeys.None, 3);
        public static readonly Hotkey DefaultRecordHotkey = new Hotkey(ModifierKeys.None, 4);

        public MouseButtonKind Button = MouseButtonKind.Left;
        public bool DoubleClick;

        /// <summary>클릭 사이 간격(ms). 1 이상이어야 시작할 수 있다.</summary>
        public int IntervalMs = 100;

        public bool CounterEnabled;
        public int CounterLimit = 100;

        /// <summary>시작할 때의 포인터 위치에서만 클릭한다(클릭 중 마우스를 움직여도 그 위치로 되돌아간다).</summary>
        public bool FreezePointer = true;

        public bool KeepOnTop;
        public bool HideWhileClicking;
        public bool ShowOnStop;

        /// <summary>기록한 위치를 차례로 돌아가며 클릭한다.</summary>
        public bool SmartClick;

        public List<ScreenPoint> Points = new List<ScreenPoint>();

        public Hotkey StartHotkey = DefaultStartHotkey;
        public Hotkey RecordHotkey = DefaultRecordHotkey;

        /// <summary>정한 시각까지 기다렸다가 시작한다.</summary>
        public bool ScheduleStartEnabled;

        /// <summary>
        /// 예약 시작 시각(하루 안의 시각). 날짜는 저장하지 않고 창을 열 때마다 오늘로 둔다
        /// (지난 날짜가 남아 있으면 매번 고쳐야 하므로).
        /// </summary>
        public TimeSpan ScheduleStartTime;

        /// <summary>정한 시각이 되면 멈춘다(until).</summary>
        public bool ScheduleUntilEnabled;

        /// <summary>종료 시각(하루 안의 시각). 날짜는 예약 시작과 같이 오늘로 둔다.</summary>
        public TimeSpan ScheduleUntilTime;

        public AutoClickSettings()
        {
            // 저장한 값이 없을 때: 다음 정각 분에 시작해서 1시간 뒤에 멈춤
            var next = DateTime.Now.AddMinutes(1);
            ScheduleStartTime = new TimeSpan(next.Hour, next.Minute, 0);
            ScheduleUntilTime = TimeSpan.FromTicks((ScheduleStartTime + TimeSpan.FromHours(1)).Ticks % TimeSpan.TicksPerDay);
        }

        public static AutoClickSettings Load(IPluginManager manager)
        {
            var s = new AutoClickSettings();
            MouseButtonKind button;
            if (Enum.TryParse(manager.GetSetting("mouseButton"), out button) && Enum.IsDefined(typeof(MouseButtonKind), button))
                s.Button = button;
            s.DoubleClick = ReadBool(manager, "doubleClick", s.DoubleClick);
            s.IntervalMs = ReadInt(manager, "intervalMs", s.IntervalMs, 0, int.MaxValue);
            s.CounterEnabled = ReadBool(manager, "counter.enabled", s.CounterEnabled);
            s.CounterLimit = ReadInt(manager, "counter.limit", s.CounterLimit, 1, int.MaxValue);
            s.FreezePointer = ReadBool(manager, "freezePointer", s.FreezePointer);
            s.KeepOnTop = ReadBool(manager, "keepOnTop", s.KeepOnTop);
            s.HideWhileClicking = ReadBool(manager, "hideWhileClicking", s.HideWhileClicking);
            s.ShowOnStop = ReadBool(manager, "showOnStop", s.ShowOnStop);
            s.SmartClick = ReadBool(manager, "smartClick", s.SmartClick);
            s.Points = ParsePoints(manager.GetSetting("smartClick.points"));
            s.StartHotkey = Hotkey.Parse(manager.GetSetting("hotkey.start"), DefaultStartHotkey);
            s.RecordHotkey = Hotkey.Parse(manager.GetSetting("hotkey.record"), DefaultRecordHotkey);
            s.ScheduleStartEnabled = ReadBool(manager, "schedule.start.enabled", s.ScheduleStartEnabled);
            s.ScheduleStartTime = ReadTimeOfDay(manager, "schedule.start.time", s.ScheduleStartTime);
            s.ScheduleUntilEnabled = ReadBool(manager, "schedule.until.enabled", s.ScheduleUntilEnabled);
            s.ScheduleUntilTime = ReadTimeOfDay(manager, "schedule.until.time", s.ScheduleUntilTime);
            return s;
        }

        /// <summary>바뀐 키만 저장한다(SetSetting은 부를 때마다 파일을 쓴다). 저장 실패는 예외로 던진다.</summary>
        public void Save(IPluginManager manager)
        {
            var values = new Dictionary<string, string>
            {
                { "mouseButton", Button.ToString() },
                { "doubleClick", Format(DoubleClick) },
                { "intervalMs", IntervalMs.ToString(CultureInfo.InvariantCulture) },
                { "counter.enabled", Format(CounterEnabled) },
                { "counter.limit", CounterLimit.ToString(CultureInfo.InvariantCulture) },
                { "freezePointer", Format(FreezePointer) },
                { "keepOnTop", Format(KeepOnTop) },
                { "hideWhileClicking", Format(HideWhileClicking) },
                { "showOnStop", Format(ShowOnStop) },
                { "smartClick", Format(SmartClick) },
                { "smartClick.points", FormatPoints(Points) },
                { "hotkey.start", StartHotkey.ToString() },
                { "hotkey.record", RecordHotkey.ToString() },
                { "schedule.start.enabled", Format(ScheduleStartEnabled) },
                { "schedule.start.time", FormatTimeOfDay(ScheduleStartTime) },
                { "schedule.until.enabled", Format(ScheduleUntilEnabled) },
                { "schedule.until.time", FormatTimeOfDay(ScheduleUntilTime) },
            };
            foreach (var pair in values)
            {
                if (manager.GetSetting(pair.Key) != pair.Value)
                    manager.SetSetting(pair.Key, pair.Value);
            }
        }

        /// <summary>"x,y;x,y" 형식. 읽지 못한 항목은 건너뛴다.</summary>
        public static List<ScreenPoint> ParsePoints(string text)
        {
            var points = new List<ScreenPoint>();
            if (string.IsNullOrEmpty(text))
                return points;
            foreach (var item in text.Split(';'))
            {
                var xy = item.Split(',');
                int x, y;
                if (xy.Length == 2
                    && int.TryParse(xy[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x)
                    && int.TryParse(xy[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y))
                    points.Add(new ScreenPoint(x, y));
            }
            return points;
        }

        public static string FormatPoints(IEnumerable<ScreenPoint> points)
        {
            return string.Join(";", points.Select(p =>
                p.X.ToString(CultureInfo.InvariantCulture) + "," + p.Y.ToString(CultureInfo.InvariantCulture)));
        }

        private static bool ReadBool(IPluginManager manager, string key, bool fallback)
        {
            bool value;
            return bool.TryParse(manager.GetSetting(key), out value) ? value : fallback;
        }

        private static int ReadInt(IPluginManager manager, string key, int fallback, int min, int max)
        {
            int value;
            if (!int.TryParse(manager.GetSetting(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                return fallback;
            return Math.Clamp(value, min, max);
        }

        /// <summary>"HH:mm:ss" 형식, 0:00:00 ~ 23:59:59.</summary>
        private static TimeSpan ReadTimeOfDay(IPluginManager manager, string key, TimeSpan fallback)
        {
            TimeSpan value;
            if (!TimeSpan.TryParseExact(manager.GetSetting(key), @"hh\:mm\:ss", CultureInfo.InvariantCulture, out value)
                || value < TimeSpan.Zero || value >= TimeSpan.FromDays(1))
                return fallback;
            return value;
        }

        private static string FormatTimeOfDay(TimeSpan value)
        {
            return value.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
        }

        private static string Format(bool value)
        {
            return value ? "true" : "false";
        }
    }
}
