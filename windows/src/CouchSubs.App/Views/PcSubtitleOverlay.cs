// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App.Views;

/// <summary>
/// Monitörde oynayanlar için bilgisayardaki altyazı: ekranın altında, her pencerenin üstünde duran saydam katman.
/// Tıklamalar altından oyuna geçer, odak almaz, görev çubuğunda ve Alt+Tab'da görünmez. Ekran görüntüsünden dışlanır;
/// böylece CouchSubs'ın kendi pencere okuması (ve yazı tanıma) bu altyazıyı yeniden okumaz. Görünümü TV'deki gibi
/// ayarlardaki yazı boyutu, zemin koyuluğu ve kalınlıktan gelir.
/// </summary>
public sealed partial class PcSubtitleOverlay : Window
{
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExLayered = 0x80000;
    private const long WsExNoActivate = 0x8000000;
    /// <summary>Windows 10 2004 ve sonrası: pencere ekran görüntüsünde ve yakalamada hiç görünmez.</summary>
    private const uint WdaExcludeFromCapture = 0x11;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    private readonly AppController controller;
    private readonly Border box;
    private readonly TextBlock label;
    private readonly DispatcherTimer timer;

    /// <summary>Katmanı denetleyicinin altyazı olaylarına bağlar; pencere ilk altyazıda açılır.</summary>
    public static void Attach(AppController controller)
    {
        PcSubtitleOverlay? overlay = null;
        controller.PcSubtitleChanged += subtitle =>
        {
            if (subtitle is null)
            {
                overlay?.HideSubtitle();
                return;
            }
            overlay ??= new PcSubtitleOverlay(controller);
            overlay.Display(subtitle);
        };
    }

    private PcSubtitleOverlay(AppController controller)
    {
        this.controller = controller;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;

        label = new TextBlock
        {
            Foreground = Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
        };
        box = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 8, 18, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = label,
            Visibility = Visibility.Collapsed,
        };
        Content = new Grid { Children = { box } };

        timer = new DispatcherTimer();
        timer.Tick += (_, _) => HideSubtitle();
        SourceInitialized += (_, _) => MakeClickThrough();
        PlaceOnScreen();
        // Ekran çözünürlüğü değişince (oyun tam ekrana geçti) katman da yeni boyuta yerleşir.
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
    }

    private void OnDisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(PlaceOnScreen));

    /// <summary>Katman birincil ekranın tamamını kaplar; altyazı ekranın altına, ayardaki yüksekliğe yerleşir.</summary>
    private void PlaceOnScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
    }

    private void MakeClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = (long)GetWindowLongPtr(handle, GwlExStyle);
        SetWindowLongPtr(handle, GwlExStyle, (IntPtr)(style | WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate));
        // Eski Windows'ta dışlama yoktur; o zaman da katman çalışır (yalnız ekran görüntüsünde görünür).
        SetWindowDisplayAffinity(handle, WdaExcludeFromCapture);
    }

    private void Display(PcSubtitle subtitle)
    {
        var style = controller.Settings.Style;
        label.Text = subtitle.Text;
        // TV'deki boyutlar 1080p ekrana göredir; bilgisayar ekranında da aynı oranda görünsün.
        label.FontSize = Math.Max(12, style.FontSize * Height / 1080);
        label.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.SemiBold;
        var alpha = (byte)Math.Round(Math.Clamp(style.Background, 0, 100) * 2.55);
        box.Background = new SolidColorBrush(Color.FromArgb(alpha, 0, 0, 0));
        // Zemin yoksa yazı oyunun üstünde gölgeyle okunur.
        label.Effect = alpha < 90 ? new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 6, ShadowDepth = 0, Opacity = 1 } : null;
        box.MaxWidth = Width * 0.8;
        box.Margin = new Thickness(0, 0, 0, Height * Math.Clamp(style.BottomPercent, 0, 85) / 100.0);
        box.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1000, subtitle.DurationMs));
        timer.Start();
    }

    private void HideSubtitle()
    {
        timer.Stop();
        box.Visibility = Visibility.Collapsed;
        label.Text = "";
    }
}
