using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace SwitchPilot.App.Services;

// Native shell notification: no service, app package, elevation or background process.
public sealed class WindowsNotifications : IDisposable
{
    private NotifyData data;
    private bool added;
    private string last = "";
    private DateTimeOffset lastAt;
    public void Show(string title, string message)
    {
        var key = title + message;
        if (key == last && DateTimeOffset.UtcNow - lastAt < TimeSpan.FromMinutes(1)) return;
        if (!added)
        {
            var handle = new WindowInteropHelper(Application.Current.MainWindow).Handle;
            if (handle == IntPtr.Zero) return;
            data = new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = handle, Id = 1, Flags = 2 | 4,
                Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)), Tip = "Switch Pilot", Info = "", Title = "" };
            added = Shell_NotifyIcon(0, ref data);
        }
        if (!added) return;
        data.Flags = 16; data.Info = message.Length > 255 ? message[..255] : message;
        data.Title = title.Length > 63 ? title[..63] : title; data.InfoFlags = 1 | 0x80; data.Timeout = 5000;
        Shell_NotifyIcon(1, ref data); last = key; lastAt = DateTimeOffset.UtcNow;
    }
    public void Dispose() { if (added) Shell_NotifyIcon(2, ref data); added = false; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
}
