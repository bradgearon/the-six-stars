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
    private const int WM_POWERBROADCAST = 0x0218;
    private const int PBT_APMSUSPEND = 0x0004;
    private const int PBT_APMRESUMECRITICAL = 0x0006;
    private const int PBT_APMRESUMESUSPEND = 0x0007;
    private const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_WIN = 0x0008;
    private HwndSource? _source;
    private MusicWindow? _musicWindow;

    private void AskForMusic_Click(object sender, RoutedEventArgs e)
    {
        if (_musicWindow != null) { _musicWindow.Activate(); return; }
        try {
            if (DataContext is not MainViewModel vm) return;
            _musicWindow = vm.CreateMusicWindow();
            _musicWindow.Owner = this;
            _musicWindow.Closed += (_, _) => _musicWindow = null;
            _musicWindow.Show();
        } catch (Exception error) {
            MessageBox.Show(this, error.Message, "Music", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

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
        if (DataContext is MainViewModel viewModel)
        {
            if (msg == WM_HOTKEY)
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
            else if (msg == WM_POWERBROADCAST)
            {
                int powerEvent = wParam.ToInt32();
                if (powerEvent == PBT_APMSUSPEND)
                {
                    viewModel.HandleSystemSuspend();
                }
                else if (powerEvent == PBT_APMRESUMEAUTOMATIC
                    || powerEvent == PBT_APMRESUMESUSPEND
                    || powerEvent == PBT_APMRESUMECRITICAL)
                {
                    _ = viewModel.HandleSystemResumeAsync();
                }
            }
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _musicWindow?.Close();
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
