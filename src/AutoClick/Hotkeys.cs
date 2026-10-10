using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace AutoClick
{
    /// <summary>수식 키 + F1~F12 단축키. 설정에는 "Ctrl+Alt+F3", "F3" 같은 문자열로 저장한다.</summary>
    internal readonly struct Hotkey : IEquatable<Hotkey>
    {
        /// <summary>화면의 수식 키 선택지. ModifierKeys 값은 RegisterHotKey의 MOD_ALT(1)·MOD_CONTROL(2)·MOD_SHIFT(4)와 같다.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, ModifierKeys>> ModifierOptions = new[]
        {
            new KeyValuePair<string, ModifierKeys>("없음", ModifierKeys.None),
            new KeyValuePair<string, ModifierKeys>("Ctrl", ModifierKeys.Control),
            new KeyValuePair<string, ModifierKeys>("Alt", ModifierKeys.Alt),
            new KeyValuePair<string, ModifierKeys>("Shift", ModifierKeys.Shift),
            new KeyValuePair<string, ModifierKeys>("Ctrl+Alt", ModifierKeys.Control | ModifierKeys.Alt),
            new KeyValuePair<string, ModifierKeys>("Ctrl+Shift", ModifierKeys.Control | ModifierKeys.Shift),
        };

        public Hotkey(ModifierKeys modifiers, int functionKey)
        {
            Modifiers = modifiers;
            FunctionKey = Math.Clamp(functionKey, 1, 12);
        }

        public ModifierKeys Modifiers { get; }

        /// <summary>1~12 (F1~F12).</summary>
        public int FunctionKey { get; }

        /// <summary>VK_F1(0x70)부터 차례로.</summary>
        public uint VirtualKey { get { return (uint)(0x6F + FunctionKey); } }

        public override string ToString()
        {
            var parts = new List<string>();
            if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
            parts.Add("F" + FunctionKey);
            return string.Join("+", parts);
        }

        /// <summary>ToString() 형식을 읽는다. 읽지 못하면 fallback.</summary>
        public static Hotkey Parse(string text, Hotkey fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
                return fallback;

            var modifiers = ModifierKeys.None;
            var parts = text.Split('+');
            for (var i = 0; i < parts.Length - 1; i++)
            {
                switch (parts[i].Trim())
                {
                    case "Ctrl": modifiers |= ModifierKeys.Control; break;
                    case "Alt": modifiers |= ModifierKeys.Alt; break;
                    case "Shift": modifiers |= ModifierKeys.Shift; break;
                    default: return fallback;
                }
            }

            var key = parts[parts.Length - 1].Trim();
            int number;
            if (key.Length < 2 || key[0] != 'F' || !int.TryParse(key.Substring(1), out number) || number < 1 || number > 12)
                return fallback;
            return new Hotkey(modifiers, number);
        }

        public bool Equals(Hotkey other)
        {
            return Modifiers == other.Modifiers && FunctionKey == other.FunctionKey;
        }

        public override bool Equals(object obj)
        {
            return obj is Hotkey && Equals((Hotkey)obj);
        }

        public override int GetHashCode()
        {
            return ((int)Modifiers << 8) | FunctionKey;
        }
    }

    /// <summary>
    /// 창 핸들에 전역 단축키를 등록하고 WM_HOTKEY를 받는다. 창을 숨겨도(Hide) 핸들은 남아 있어서 계속 받는다.
    /// 등록은 이 창이 열려 있는 동안만 유지되고, Dispose하면 모두 해제한다.
    /// </summary>
    internal sealed class HotkeyHost : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _handlers = new Dictionary<int, Action>();

        public HotkeyHost(HwndSource source)
        {
            _source = source;
            _source.AddHook(WndProc);
        }

        /// <summary>id에 단축키를 등록한다(이미 있으면 바꾼다). 성공하면 null, 실패하면 Windows 오류 메시지.</summary>
        public string Register(int id, Hotkey hotkey, Action handler)
        {
            Unregister(id);
            if (!NativeMethods.RegisterHotKey(_source.Handle, id, (uint)hotkey.Modifiers | NativeMethods.MOD_NOREPEAT, hotkey.VirtualKey))
                return new Win32Exception(Marshal.GetLastWin32Error()).Message;
            _handlers[id] = handler;
            return null;
        }

        public void Unregister(int id)
        {
            if (_handlers.Remove(id))
                NativeMethods.UnregisterHotKey(_source.Handle, id);
        }

        public bool IsRegistered(int id)
        {
            return _handlers.ContainsKey(id);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            Action handler;
            if (msg == NativeMethods.WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out handler))
            {
                handled = true;
                handler();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (var id in new List<int>(_handlers.Keys))
                Unregister(id);
            _source.RemoveHook(WndProc);
        }
    }
}
