using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wds.SixStars.Ui.ViewModels;

namespace Wds.SixStars.Ui;

public partial class MainWindow : Window
{
    private const int HotkeyToggleRecord = 0x5E01;
    private const int HotkeyCancelAll = 0x5E02;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_WIN = 0x0008;
    private HwndSource? _source;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);

        RegisterHotKey(handle, HotkeyToggleRecord, MOD_CONTROL | MOD_WIN, (uint)KeyInterop.VirtualKeyFromKey(Key.E));
        RegisterHotKey(handle, HotkeyCancelAll, MOD_CONTROL | MOD_WIN, (uint)KeyInterop.VirtualKeyFromKey(Key.C));
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && DataContext is MainViewModel viewModel)
        {
            int id = wParam.ToInt32();
            if (id == HotkeyToggleRecord)
            {
                if (viewModel.ToggleRecordingCommand.CanExecute(null))
                {
                    viewModel.ToggleRecordingCommand.Execute(null);
                }
                handled = true;
            }
            else if (id == HotkeyCancelAll)
            {
                viewModel.CancelAllRuntimeOperations("Win+Ctrl+C hotkey.");
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        UnregisterHotKey(handle, HotkeyToggleRecord);
        UnregisterHotKey(handle, HotkeyCancelAll);
        _source?.RemoveHook(WndProc);

        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }

        base.OnClosed(e);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
