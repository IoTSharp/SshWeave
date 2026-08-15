using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SshWeave.Desktop.Windows;

internal sealed unsafe partial class WindowsTrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8001;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDoubleClick = 0x0203;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmRButtonUp = 0x0205;
    private const uint NimAdd = 0x00000000;
    private const uint NimModify = 0x00000001;
    private const uint NimDelete = 0x00000002;
    private const uint NimSetVersion = 0x00000004;
    private const uint NifMessage = 0x00000001;
    private const uint NifIcon = 0x00000002;
    private const uint NifTip = 0x00000004;
    private const uint NotifyIconVersion4 = 4;
    private const uint MfString = 0x00000000;
    private const uint MfSeparator = 0x00000800;
    private const uint TpmRightButton = 0x0002;
    private const uint TpmReturnCommand = 0x0100;
    private const uint MenuOpen = 1001;
    private const uint MenuToggleConnection = 1002;
    private const uint MenuExit = 1003;
    private const int IdiShield = 32518;
    private const int IdiApplication = 32512;
    private const uint ImageIcon = 1;
    private const uint LrShared = 0x00008000;
    private static readonly string WindowClassName = $"SshWeave.Tray.{Environment.ProcessId}";
    private static WindowsTrayIcon? s_current;

    private readonly Action _showWindow;
    private readonly Action _toggleConnection;
    private readonly Action _exit;
    private readonly Func<bool> _isConnected;
    private nint _windowHandle;
    private bool _installed;

    public WindowsTrayIcon(
        Action showWindow,
        Action toggleConnection,
        Action exit,
        Func<bool> isConnected)
    {
        _showWindow = showWindow;
        _toggleConnection = toggleConnection;
        _exit = exit;
        _isConnected = isConnected;
    }

    public void Install(string tooltip)
    {
        if (_installed)
        {
            return;
        }

        fixed (char* className = WindowClassName)
        {
            WindowClass windowClass = new()
            {
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = GetModuleHandleW(null),
                ClassName = className,
            };
            _ = RegisterClassW(ref windowClass);
        }

        _windowHandle = CreateWindowExW(
            0,
            WindowClassName,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            new nint(-3),
            0,
            GetModuleHandleW(null),
            0);
        if (_windowHandle == 0)
        {
            throw new InvalidOperationException("无法创建托盘消息窗口。");
        }

        s_current = this;
        NotifyIconData data = CreateData(tooltip);
        if (!ShellNotifyIconW(NimAdd, ref data))
        {
            Dispose();
            throw new InvalidOperationException("Windows 拒绝创建 SshWeave 托盘图标。");
        }

        data.TimeoutOrVersion = NotifyIconVersion4;
        _ = ShellNotifyIconW(NimSetVersion, ref data);
        _installed = true;
    }

    public void SetTooltip(string tooltip)
    {
        if (!_installed)
        {
            return;
        }

        NotifyIconData data = CreateData(tooltip);
        data.Flags = NifTip;
        _ = ShellNotifyIconW(NimModify, ref data);
    }

    public void Dispose()
    {
        if (_windowHandle != 0)
        {
            if (_installed)
            {
                NotifyIconData data = CreateData(string.Empty);
                _ = ShellNotifyIconW(NimDelete, ref data);
            }

            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }

        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }

        _installed = false;
    }

    private NotifyIconData CreateData(string tooltip)
    {
        NotifyIconData data = new()
        {
            Size = (uint)sizeof(NotifyIconData),
            WindowHandle = _windowHandle,
            Identifier = 1,
            Flags = NifMessage | NifIcon | NifTip,
            CallbackMessage = CallbackMessage,
            IconHandle = LoadApplicationIcon(),
        };
        char* destination = data.Tooltip;
        WriteFixedString(destination, 128, tooltip);
        return data;
    }

    private static nint LoadApplicationIcon()
    {
        nint icon = LoadImageW(
            GetModuleHandleW(null),
            new nint(IdiApplication),
            ImageIcon,
            16,
            16,
            LrShared);
        return icon != 0 ? icon : LoadIconW(0, new nint(IdiShield));
    }

    private void ShowContextMenu()
    {
        nint menu = CreatePopupMenu();
        if (menu == 0)
        {
            return;
        }

        try
        {
            _ = AppendMenuW(menu, MfString, MenuOpen, "打开 SshWeave");
            _ = AppendMenuW(
                menu,
                MfString,
                MenuToggleConnection,
                _isConnected() ? "断开连接" : "开始连接");
            _ = AppendMenuW(menu, MfSeparator, 0, null);
            _ = AppendMenuW(menu, MfString, MenuExit, "退出");
            _ = GetCursorPos(out Point point);
            _ = SetForegroundWindow(_windowHandle);
            uint command = TrackPopupMenu(
                menu,
                TpmRightButton | TpmReturnCommand,
                point.X,
                point.Y,
                0,
                _windowHandle,
                0);
            _ = PostMessageW(_windowHandle, 0, 0, 0);
            switch (command)
            {
                case MenuOpen:
                    _showWindow();
                    break;
                case MenuToggleConnection:
                    _toggleConnection();
                    break;
                case MenuExit:
                    _exit();
                    break;
            }
        }
        finally
        {
            _ = DestroyMenu(menu);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint windowHandle, uint message, nuint wParam, nint lParam)
    {
        if (message == CallbackMessage && s_current is not null)
        {
            uint trayMessage = (uint)lParam & 0xffff;
            if (trayMessage is WmLButtonUp or WmLButtonDoubleClick)
            {
                s_current._showWindow();
                return 0;
            }
            if (trayMessage is WmRButtonUp or WmContextMenu)
            {
                s_current.ShowContextMenu();
                return 0;
            }
        }

        return DefWindowProcW(windowHandle, message, wParam, lParam);
    }

    private static void WriteFixedString(char* destination, int capacity, string value)
    {
        int length = Math.Min(value.Length, capacity - 1);
        for (int index = 0; index < length; index++)
        {
            destination[index] = value[index];
        }
        destination[length] = '\0';
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowClass
    {
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtraBytes;
        public int WindowExtraBytes;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint BackgroundBrush;
        public char* MenuName;
        public char* ClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint WindowHandle;
        public uint Identifier;
        public uint Flags;
        public uint CallbackMessage;
        public nint IconHandle;
        public fixed char Tooltip[128];
        public uint State;
        public uint StateMask;
        public fixed char Info[256];
        public uint TimeoutOrVersion;
        public fixed char InfoTitle[64];
        public uint InfoFlags;
        public Guid ItemGuid;
        public nint BalloonIconHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? moduleName);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial ushort RegisterClassW(ref WindowClass windowClass);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint windowHandle, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial nint LoadIconW(nint instance, nint iconName);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    private static partial nint LoadImageW(
        nint instance,
        nint name,
        uint type,
        int desiredWidth,
        int desiredHeight,
        uint loadFlags);

    [LibraryImport("shell32.dll", EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint identifier, string? text);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenu(
        nint menu,
        uint flags,
        int x,
        int y,
        int reserved,
        nint windowHandle,
        nint rectangle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint windowHandle);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint windowHandle, uint message, nuint wParam, nint lParam);
}
