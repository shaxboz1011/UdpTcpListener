using System.Diagnostics;
using System.Runtime.InteropServices;

namespace UdpTcpListener.App.Services;

/// <summary>Sound and operating-system notifications for alerts. Best effort: failures are ignored.</summary>
public static class AlertEffects
{
    private static readonly string[] LinuxSounds =
    [
        "/usr/share/sounds/freedesktop/stereo/message-new-instant.oga",
        "/usr/share/sounds/freedesktop/stereo/bell.oga",
        "/usr/share/sounds/freedesktop/stereo/complete.oga",
    ];

    private static long _lastSound;

    public static void PlaySound()
    {
        // Several alerts in one burst should not become a machine gun.
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastSound) < 700)
            return;
        Interlocked.Exchange(ref _lastSound, now);

        if (OperatingSystem.IsWindows())
        {
            MessageBeep(0x30); // MB_ICONEXCLAMATION: the user's "Exclamation" system sound
            return;
        }

        var file = LinuxSounds.FirstOrDefault(File.Exists);
        if (file is not null && (Run("paplay", file) || Run("pw-play", file)))
            return;
        if (Run("canberra-gtk-play", "-i", "message-new-instant"))
            return;
        Console.Beep();
    }

    /// <summary>Desktop notification outside the app window (Linux: notify-send). Returns false when unavailable.</summary>
    public static bool ShowDesktopNotification(string title, string body) =>
        OperatingSystem.IsLinux() && Run("notify-send", "--app-name=UdpTcpListener", "--icon=dialog-warning", title, body);

    /// <summary>Flashes the taskbar button until the window is activated (Windows).</summary>
    public static void FlashTaskbar(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero)
            return;
        var info = new FlashWInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWInfo>(),
            Hwnd = hwnd,
            Flags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG
            Count = 5,
        };
        FlashWindowEx(ref info);
    }

    private static bool Run(string program, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
            foreach (var arg in args)
                start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return false; // program not installed
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWInfo
    {
        public uint Size;
        public IntPtr Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWInfo info);
}
