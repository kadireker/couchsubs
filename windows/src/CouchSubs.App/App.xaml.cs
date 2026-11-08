// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Windows;
using CouchSubs.App.Services;
using CouchSubs.App.Theme;
using CouchSubs.Core;
using CouchSubs.Shell;

namespace CouchSubs.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var renderFolder = e.Args is ["--render-screens", var folder] ? folder : null;
        // Eksik bir DLL, onu kullanan yöntem derlenirken hata verir. Bu yüzden denetim ve yakalama burada, uygulamanın
        // kendi türlerine dokunan kod ise ayrı yöntemde (Start) durur.
        var missing = StartupGuard.MissingFiles(AppContext.BaseDirectory);
        if (missing.Count > 0)
        {
            StartupGuard.Fail(this, StartupGuard.MissingMessage(missing), null, renderFolder);
            return;
        }
        if (renderFolder is null) StartupGuard.Watch(this);
        try
        {
            Start(renderFolder);
        }
        catch (Exception error)
        {
            StartupGuard.Fail(this, StartupGuard.ErrorMessage(error), error, renderFolder);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    void Start(string? renderFolder)
    {
        if (renderFolder is not null)
        {
            ScreenRenderer.Run(this, renderFolder);
            return;
        }
        RemotePlayDiagnostics.Start();
        ThemeManager.Apply(Resources);
        ThemeManager.Follow(Resources);
        var services = new LiveServices();
        var controller = new AppController(services, new DpapiAppStore(), new FileSettingsStore(), new WindowsSleepGuard(),
            Localizer.Current, action => Dispatcher.BeginInvoke(action))
        {
            // Samsung'un sertifika için giriş sayfası varsayılan tarayıcıda açılır.
            OpenInBrowser = url => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }),
        };
        var window = new ShellWindow(controller);
        // Pencere seçici (pencere kaynağı) ana pencerenin üstünde açılır.
        services.OwnerWindow = () => new System.Windows.Interop.WindowInteropHelper(window).Handle;
        MainWindow = window;
        window.Show();
    }
}
