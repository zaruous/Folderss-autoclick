using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutoClick
{
    /// <summary>
    /// 마우스 입력·전역 단축키에 쓰는 Win32 함수.
    /// 좌표는 모두 같은 프로세스(Folderss)의 DPI 기준이라, GetCursorPos로 기록한 위치를 SetCursorPos로 그대로 되돌릴 수 있다.
    /// </summary>
    internal static class NativeMethods
    {
        public const int WM_HOTKEY = 0x0312;

        /// <summary>단축키를 누르고 있을 때 WM_HOTKEY가 반복해서 오지 않게 한다.</summary>
        public const uint MOD_NOREPEAT = 0x4000;

        private const uint INPUT_MOUSE = 0;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        // INPUT은 MOUSEINPUT·KEYBDINPUT·HARDWAREINPUT의 공용체지만, 마우스만 쓰므로 가장 큰 MOUSEINPUT만 둔다.
        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint Type;
            public MOUSEINPUT Mouse;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int Dx;
            public int Dy;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetCursorPos(int x, int y);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint count, INPUT[] inputs, int size);

        /// <summary>타이머 해상도를 1ms로 올린다. 기본 15.6ms로는 1/100초 단위 간격을 지킬 수 없다.</summary>
        [DllImport("winmm.dll")]
        public static extern uint timeBeginPeriod(uint milliseconds);

        [DllImport("winmm.dll")]
        public static extern uint timeEndPeriod(uint milliseconds);

        public static ScreenPoint GetCursorPosition()
        {
            POINT point;
            if (!GetCursorPos(out point))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new ScreenPoint(point.X, point.Y);
        }

        public static void MoveCursor(ScreenPoint point)
        {
            if (!SetCursorPos(point.X, point.Y))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// 지금 포인터 위치에서 누름·뗌을 보낸다(더블 클릭이면 두 번). 잠긴 화면 등에서 막히면 예외.
        /// 관리자 권한 창에는 UIPI 때문에 입력이 가지 않지만, 이 경우는 Windows가 실패로 알려 주지 않는다.
        /// </summary>
        public static void Click(MouseButtonKind button, bool doubleClick)
        {
            uint down, up;
            switch (button)
            {
                case MouseButtonKind.Right:
                    down = MOUSEEVENTF_RIGHTDOWN;
                    up = MOUSEEVENTF_RIGHTUP;
                    break;
                case MouseButtonKind.Middle:
                    down = MOUSEEVENTF_MIDDLEDOWN;
                    up = MOUSEEVENTF_MIDDLEUP;
                    break;
                default:
                    down = MOUSEEVENTF_LEFTDOWN;
                    up = MOUSEEVENTF_LEFTUP;
                    break;
            }

            var inputs = new INPUT[doubleClick ? 4 : 2];
            for (var i = 0; i < inputs.Length; i++)
            {
                inputs[i].Type = INPUT_MOUSE;
                inputs[i].Mouse.Flags = i % 2 == 0 ? down : up;
            }
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}
