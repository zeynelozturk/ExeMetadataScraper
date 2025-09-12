using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using WinRT.Interop;

namespace WinUIMetadataScraper
{
    public partial class App : Application
    {
        public App()
        {
            InitializeComponent();
        }

        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            m_window = new MainWindow();
            m_window.Activate();

            // Apply after activation so OS restore doesn’t override it.
            m_window.DispatcherQueue.TryEnqueue(() =>
                SetInitialClientSizeAndCenter(m_window,
                    dipWidth: 600, dipHeight: 880,
                    minDipWidth: 500, minDipHeight: 640));
        }

        private static void SetInitialClientSizeAndCenter(Window window, int dipWidth, int dipHeight, int minDipWidth, int minDipHeight)
        {
            var hWnd = WindowNative.GetWindowHandle(window);
            var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            if (appWindow is null) return;

            // If the OS restored the window maximized, normalize first.
            if (appWindow.Presenter is OverlappedPresenter p)
            {
                try { p.Restore(); } catch { /* ignore */ }
            }

            // Reliable per-monitor DPI scale (avoids XamlRoot timing issues)
            double scale = GetDpiForWindow(hWnd) / 96.0;

            int dipW = Math.Max(dipWidth, minDipWidth);
            int dipH = Math.Max(dipHeight, minDipHeight);

            int clientPxW = (int)Math.Round(dipW * scale);
            int clientPxH = (int)Math.Round(dipH * scale);

            var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;

            // Ensure not oversized and not too small relative to screen
            int minHeightPxByRatio = (int)(workArea.Height * 0.58); // keep at least ~58% of work area height
            clientPxW = Math.Min(clientPxW, Math.Max(600, workArea.Width - 40));  // margin from edges
            clientPxH = Math.Max(clientPxH, minHeightPxByRatio);
            clientPxH = Math.Min(clientPxH, Math.Max(400, workArea.Height - 80));

            appWindow.ResizeClient(new SizeInt32(clientPxW, clientPxH));

            var outer = appWindow.Size;
            int x = workArea.X + (workArea.Width - outer.Width) / 2;
            int y = workArea.Y + (workArea.Height - outer.Height) / 2;
            appWindow.Move(new PointInt32(x, y));
        }

        [DllImport("User32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hWnd);

        private Window? m_window;
    }
}
