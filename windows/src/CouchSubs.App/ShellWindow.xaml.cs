// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using CouchSubs.App.Services;
using CouchSubs.App.Views;
using CouchSubs.Shell;
using Microsoft.Win32;

namespace CouchSubs.App;

/// <summary>Uygulamanın tek penceresi: kurulum bitmediyse sihirbaz, bittiyse ana ekran.</summary>
public partial class ShellWindow : Window
{
    private readonly AppController controller;
    private bool showingMain;
    private GlobalHotkeys? hotkeys;

    public ShellWindow(AppController controller)
    {
        this.controller = controller;
        InitializeComponent();
        controller.PropertyChanged += OnChanged;
        // Bilgisayar uykudan uyanınca bağlantılar hemen yenilenir.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        // Num 0, 1, 2, 3, 5, 8, 9 kısayolları yalnız çeviri sürerken tutulur.
        SourceInitialized += (_, _) =>
        {
            hotkeys = new GlobalHotkeys(new WindowInteropHelper(this).Handle, OnHotkey);
            hotkeys.Enabled = controller.IsRunning;
        };
        // Bilgisayarda gösterilecek çeviriler küçük, üstte duran bir kutuda çıkar.
        controller.PcResultReady += PcResultWindow.Display;
        // Pencere kaynağında altyazı monitörde de, oyunun üstündeki saydam katmanda gösterilir.
        PcSubtitleOverlay.Attach(controller);
        Closed += (_, _) =>
        {
            controller.PcResultReady -= PcResultWindow.Display;
            hotkeys?.Dispose();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            controller.Dispose();
        };
        ShowScreen(force: true);
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppController.Step)) ShowScreen();
        if (e.PropertyName == nameof(AppController.IsRunning) && hotkeys is not null) hotkeys.Enabled = controller.IsRunning;
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.ExtraRegion1: _ = controller.TranslateExtraRegionAsync(1); break;
            case HotkeyAction.ExtraRegion2: _ = controller.TranslateExtraRegionAsync(2); break;
            case HotkeyAction.ExtraRegion3: _ = controller.TranslateExtraRegionAsync(3); break;
            case HotkeyAction.PauseSubtitles: controller.ToggleSubtitles(); break;
            case HotkeyAction.Snapshot: controller.RequestSnapshot(); break;
            case HotkeyAction.FreeArea: FreeAreaWindow.Open(controller); break;
            case HotkeyAction.Screen: _ = controller.TranslateScreenAsync(); break;
        }
    }

    private void ShowScreen(bool force = false)
    {
        var main = controller.Step == OnboardingStep.Done;
        if (!force && main == showingMain) return;
        showingMain = main;
        Host.Content = main ? new MainView(controller) : new OnboardingView(controller);
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.Invoke(controller.SystemResumed);
    }
}
