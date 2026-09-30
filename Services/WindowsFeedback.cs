using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace CarriolaConverter;

internal static class CompletionNotification
{
    private static Window? _window;
    private static bool _attempted;
    private static bool _registered;
    private static string? _registrationWarning;

    public static void Initialize(Window window)
    {
        if (_attempted) return;
        _attempted = true;
        try
        {
            uint length = 0;
            // O fluxo MSIX existente usa a identidade do pacote.
            // 15700 = APPMODEL_ERROR_NO_PACKAGE: registrar a versão tradicional.
            if (GetCurrentPackageFullName(ref length, IntPtr.Zero) != 15700) return;
            if (!AppNotificationManager.IsSupported())
            {
                _registrationWarning = "As notificações não estão disponíveis neste Windows. O resumo aparece no aplicativo.";
                return;
            }

            var manager = AppNotificationManager.Default;
            _window = window;
            manager.NotificationInvoked += NotificationInvoked;
            try
            {
                string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Square150x150Logo.scale-100.png");
                if (File.Exists(icon)) manager.Register("Carriola Images Converter", new Uri(icon));
                else manager.Register();
                _registered = true;
            }
            catch
            {
                manager.NotificationInvoked -= NotificationInvoked;
                _window = null;
                throw;
            }
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            _registrationWarning = "Não foi possível registrar as notificações. O resumo aparece no aplicativo.";
        }
    }

    private static void NotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        var window = _window;
        if (window is null) return;
        try
        {
            window.DispatcherQueue.TryEnqueue(() =>
            {
                if (!ReferenceEquals(_window, window)) return;
                try
                {
                    var handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
                    if (IsIconic(handle)) ShowWindow(handle, 9); // SW_RESTORE
                    window.Activate();
                    SetForegroundWindow(handle);
                }
                catch (Exception ex) { Trace.WriteLine(ex); }
            });
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    public static void Shutdown()
    {
        _window = null;
        if (!_registered) return;
        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked -= NotificationInvoked;
            manager.Unregister();
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
        _registered = false;
    }

    public static string? Show(string title, string message)
    {
        if (_registrationWarning is not null) return _registrationWarning;
        try
        {
            var notification = new AppNotificationBuilder().AddText(title).AddText(message).BuildNotification();
            AppNotificationManager.Default.Show(notification);
            return notification.Id == 0 ? "O Windows não aceitou a notificação. O resumo está disponível no aplicativo." : null;
        }
        catch (Exception ex)
        {
            Trace.WriteLine(ex);
            return "Não foi possível enviar a notificação do Windows. O resumo está disponível no aplicativo.";
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetCurrentPackageFullName(ref uint length, IntPtr packageFullName);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}

internal sealed class TaskbarProgress : IDisposable
{
    private readonly IntPtr _window;
    private readonly SubclassProc _callback;
    private readonly uint _createdMessage;
    private static readonly UIntPtr SubclassId = new(0x43415252);
    private ITaskbarList3? _taskbar;
    private bool _attached;
    private bool _ready;
    private TaskbarState _state;
    private ulong _value;

    public TaskbarProgress(IntPtr window)
    {
        _window = window;
        _callback = WindowProc;
        _createdMessage = RegisterWindowMessage("TaskbarButtonCreated");
        _attached = _createdMessage != 0 && SetWindowSubclass(window, _callback, SubclassId, UIntPtr.Zero);
    }

    public void Set(double percent, TaskbarState state = TaskbarState.Normal)
    {
        _value = (ulong)Math.Round(Math.Clamp(double.IsFinite(percent) ? percent : 0, 0, 100) * 100);
        _state = state;
        Apply();
    }
    public void Clear() { _state = TaskbarState.None; Apply(); }

    private IntPtr WindowProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data)
    {
        try
        {
            if (message == _createdMessage)
            {
                ReleaseCom();
                object instance = new TaskbarListCom();
                _taskbar = (ITaskbarList3)instance;
                _taskbar.HrInit();
                _ready = true;
                Apply();
            }
            else if (message == 0x0082) // WM_NCDESTROY
            {
                RemoveWindowSubclass(window, _callback, id);
                _attached = false;
                _ready = false;
            }
        }
        catch (Exception ex) { Trace.WriteLine(ex); _ready = false; }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    private void Apply()
    {
        if (!_ready || _taskbar is null) return;
        try
        {
            _taskbar.SetProgressState(_window, _state);
            if (_state is TaskbarState.Normal or TaskbarState.Error or TaskbarState.Paused)
                _taskbar.SetProgressValue(_window, _value, 10_000);
        }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    public void Dispose()
    {
        Clear();
        if (_attached) RemoveWindowSubclass(_window, _callback, SubclassId);
        _attached = false;
        _ready = false;
        ReleaseCom();
    }

    private void ReleaseCom()
    {
        var previous = _taskbar;
        _taskbar = null;
        _ready = false;
        if (previous is null) return;
        try { Marshal.FinalReleaseComObject(previous); }
        catch (Exception ex) { Trace.WriteLine(ex); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("comctl32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id, UIntPtr data);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr window, SubclassProc callback, UIntPtr id);
    [DllImport("comctl32.dll", ExactSpelling = true)]
    private static extern IntPtr DefSubclassProc(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [ComImport, Guid("56FDF344-FD6D-11D0-958A-006097C9A090"), ClassInterface(ClassInterfaceType.None)]
    private class TaskbarListCom { }

    // Prefixo da vtable de ITaskbarList3, preservando a ordem de ITaskbarList e ITaskbarList2.
    [ComImport, Guid("EA1AFB91-9E28-4B86-90E9-9E9F8A5EEFAF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        void HrInit();
        void AddTab(IntPtr window);
        void DeleteTab(IntPtr window);
        void ActivateTab(IntPtr window);
        void SetActiveAlt(IntPtr window);
        void MarkFullscreenWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool fullscreen);
        void SetProgressValue(IntPtr window, ulong completed, ulong total);
        void SetProgressState(IntPtr window, TaskbarState state);
    }
}

internal enum TaskbarState : uint { None = 0, Indeterminate = 1, Normal = 2, Error = 4, Paused = 8 }
