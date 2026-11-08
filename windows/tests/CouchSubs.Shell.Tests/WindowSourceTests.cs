// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.LgTv;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;

namespace CouchSubs.Shell.Tests;

/// Pencere kaynağı: Xbox uygulaması, xbox.com/play, yakalama kartı önizlemesi ya da PC oyunu bu bilgisayardaki bir pencereden okunur.
public class WindowSourceTests
{
    private static readonly Reading Line = new([new TextLine("Hold the gate.", new TextBox(0.4, 0.85, 0.6, 0.9))]);

    private static (AppController Controller, FakeServices Services, MemoryStore Store) WindowMode(FakeServices? services = null, bool showOnPc = true)
    {
        services ??= new FakeServices();
        var store = new MemoryStore();
        var settings = new MemorySettings();
        settings.Save(new UserSettings { Source = VideoSource.Window, ShowOnPc = showOnPc });
        return (AppControllerTests.Make(services, store, settings), services, store);
    }

    /// Kareler hat tarafından okunana kadar gönderilir (okuma iş parçacığı havuzunda, kareler atlanabilir).
    private static async Task EmitUntil(FakeStream stream, Func<bool> done)
    {
        for (var i = 0; i < 300 && !done(); i++)
        {
            stream.Emit(Frame.Blank(64, 64));
            await Task.Delay(10);
        }
        Assert.True(done());
    }

    [Fact]
    public void WindowModeNeedsNoPs5Setup() => Assert.Equal(OnboardingStep.Done, WindowMode().Controller.Step);

    [Fact]
    public async Task WindowModeStartsWithoutConsoleAccountOrTv()
    {
        var (controller, services, _) = WindowMode();
        var window = new FakeCaptureTarget("Xbox");
        services.Windows.Enqueue(window);
        await controller.StartAsync();
        Assert.True(controller.IsRunning);
        Assert.Equal(1, services.Picks);
        Assert.True(Assert.Single(window.Streams).Started);
        Assert.Empty(services.Streams);
        Assert.Empty(services.Keepers);
        Assert.False(controller.ShowsTvStatus);
        controller.Dispose();
    }

    [Fact]
    public async Task CancelledPickerDoesNotStart()
    {
        var (controller, services, _) = WindowMode();
        await controller.StartAsync();
        Assert.False(controller.IsRunning);
        Assert.False(controller.IsStarting);
        Assert.Equal(1, services.Picks);
        controller.Dispose();
    }

    [Fact]
    public async Task ChosenWindowIsRememberedUntilItCloses()
    {
        var (controller, services, _) = WindowMode();
        var window = new FakeCaptureTarget("Xbox");
        services.Windows.Enqueue(window);
        await controller.StartAsync();
        controller.Stop();
        await controller.StartAsync();
        Assert.Equal(1, services.Picks);
        Assert.Equal(2, window.Streams.Count);
        controller.Stop();

        window.IsClosed = true;
        var other = new FakeCaptureTarget("Microsoft Edge");
        services.Windows.Enqueue(other);
        await controller.StartAsync();
        Assert.Equal(2, services.Picks);
        Assert.Single(other.Streams);
        Assert.Equal("Microsoft Edge", controller.CaptureTitle);
        controller.Dispose();
    }

    [Fact]
    public async Task FramesFromTheWindowAreTranslatedAndShownOnThePc()
    {
        var (controller, services, _) = WindowMode();
        var window = new FakeCaptureTarget("Xbox");
        services.Windows.Enqueue(window);
        var shown = new List<PcSubtitle?>();
        controller.PcSubtitleChanged += subtitle => { lock (shown) shown.Add(subtitle); };
        services.ReaderLines = Line;
        await controller.StartAsync();

        await EmitUntil(window.Streams[0], () => { lock (shown) return shown.Any(s => s?.Text == "TR: Hold the gate."); });
        Assert.Equal("TR: Hold the gate.", controller.LastTranslation);
        PcSubtitle subtitle;
        lock (shown) subtitle = shown.Last(s => s is not null)!;
        // Katman da TV gibi oyundaki altyazının yerini alır.
        Assert.NotNull(subtitle.Box);

        // Oyundaki altyazı kalkınca katmandaki de kalkar.
        services.ReaderLines = null;
        await EmitUntil(window.Streams[0], () => { lock (shown) return shown[^1] is null; });
        controller.Dispose();
    }

    [Fact]
    public async Task OverlayIsOffWhenTheSettingIsOff()
    {
        var (controller, services, _) = WindowMode(showOnPc: false);
        services.Windows.Enqueue(new FakeCaptureTarget("Xbox"));
        var raised = 0;
        controller.PcSubtitleChanged += _ => raised++;
        await controller.StartAsync();
        controller.SendTestSubtitle();
        Assert.Equal(0, raised);
        controller.Dispose();
    }

    [Fact]
    public async Task TurningTheOverlayOffClearsIt()
    {
        var (controller, services, _) = WindowMode();
        services.Windows.Enqueue(new FakeCaptureTarget("Xbox"));
        var shown = new List<PcSubtitle?>();
        controller.PcSubtitleChanged += shown.Add;
        await controller.StartAsync();
        controller.SendTestSubtitle();
        Assert.NotNull(Assert.Single(shown));
        controller.Settings = controller.Settings with { ShowOnPc = false };
        Assert.Null(shown[^1]);
        controller.Dispose();
    }

    [Fact]
    public async Task ClosedWindowIsExplainedAndClearsTheOverlay()
    {
        var (controller, services, _) = WindowMode();
        var window = new FakeCaptureTarget("Xbox");
        services.Windows.Enqueue(window);
        var shown = new List<PcSubtitle?>();
        controller.PcSubtitleChanged += shown.Add;
        await controller.StartAsync();
        window.Streams[0].Emit(SessionState.Streaming);
        Assert.Equal("Xbox", controller.Ps5StatusText);
        Assert.Equal("Window", controller.SourceTitle);
        window.Streams[0].Emit(SessionState.Disconnected(DisconnectKind.WindowClosed));
        Assert.Equal("The window was closed. Choose a window again.", controller.Ps5StatusText);
        Assert.Null(Assert.Single(shown));
        controller.Dispose();
    }

    [Fact]
    public async Task ChoosingAnotherWindowWhileRunningSwitchesTheSession()
    {
        var (controller, services, _) = WindowMode();
        var first = new FakeCaptureTarget("Xbox");
        var second = new FakeCaptureTarget("OBS");
        services.Windows.Enqueue(first);
        services.Windows.Enqueue(second);
        await controller.StartAsync();
        await controller.ChooseWindowAsync();
        Assert.False(first.Streams[0].Started);
        Assert.True(Assert.Single(second.Streams).Started);
        // Eski oturumdan geç gelen durum yeni pencerenin durumunu bozmaz.
        first.Streams[0].Emit(SessionState.Disconnected(DisconnectKind.WindowClosed));
        Assert.Equal("OBS", controller.Ps5StatusText);
        controller.Dispose();
    }

    [Fact]
    public async Task ConfiguredTvIsKeptOpenInWindowMode()
    {
        var (controller, services, store) = WindowMode();
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "127.0.0.1"));
        store.Save(StoreKeys.TvKey, "tv-key");
        // TV kayıtlıysa denetleyici açılışta okur.
        var settings = new MemorySettings();
        settings.Save(controller.Settings);
        controller.Dispose();
        controller = AppControllerTests.Make(services, store, settings);
        services.Windows.Enqueue(new FakeCaptureTarget("Xbox"));
        await controller.StartAsync();
        Assert.True(Assert.Single(services.Keepers).Running);
        Assert.True(controller.ShowsTvStatus);
        controller.Dispose();
    }

    [Fact]
    public async Task Ps5ModeDoesNotPickAWindowOrShowTheOverlay()
    {
        var services = new FakeServices();
        var store = new MemoryStore();
        store.Save(StoreKeys.AccountId, Enumerable.Repeat((byte)1, 8).ToArray());
        store.Save(StoreKeys.Console, new RegisteredConsole("192.168.1.253", "PS5", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray()));
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "127.0.0.1"));
        store.Save(StoreKeys.TvKey, "tv-key");
        var controller = AppControllerTests.Make(services, store);
        var raised = 0;
        controller.PcSubtitleChanged += _ => raised++;
        await controller.StartAsync();
        controller.SendTestSubtitle();
        Assert.Equal(0, services.Picks);
        Assert.Single(services.Streams);
        Assert.Single(services.Keepers);
        Assert.Equal(0, raised);
        Assert.Equal("PS5", controller.SourceTitle);
        controller.Dispose();
    }

    [Fact]
    public void Ps5ModeStillNeedsTheConsole()
    {
        var controller = AppControllerTests.Make();
        Assert.Equal(OnboardingStep.Welcome, controller.Step);
        Assert.False(controller.IsWindowSource);
    }

    [Fact]
    public void WelcomeCanSkipThePs5Setup()
    {
        var controller = AppControllerTests.Make();
        controller.UseWindowSource();
        Assert.True(controller.IsWindowSource);
        Assert.Equal(OnboardingStep.Tv, controller.Step);
        // TV isteğe bağlı: TV'siz devam edilir, deneme adımı (TV'de gösterilir) atlanır.
        Assert.True(controller.CanAdvance);
        controller.Advance();
        Assert.Equal(OnboardingStep.Translation, controller.Step);
        controller.Advance();
        Assert.Equal(OnboardingStep.Done, controller.Step);
    }

    [Fact]
    public void BackFromTheTvStepReturnsToWelcomeAndContinueMeansPs5()
    {
        var controller = AppControllerTests.Make();
        controller.UseWindowSource();
        controller.Back();
        Assert.Equal(OnboardingStep.Welcome, controller.Step);
        controller.Advance();
        Assert.False(controller.IsWindowSource);
        Assert.Equal(OnboardingStep.SecondAccount, controller.Step);
    }

    [Fact]
    public void SwitchingToPs5WithoutSetupOpensTheWizard()
    {
        var (controller, _, _) = WindowMode();
        controller.ChooseSource(VideoSource.Ps5);
        Assert.Equal(OnboardingStep.Welcome, controller.Step);
        Assert.False(controller.IsWindowSource);
    }

    [Fact]
    public async Task SwitchingSourceStopsTheTranslation()
    {
        var (controller, services, _) = WindowMode();
        services.Windows.Enqueue(new FakeCaptureTarget("Xbox"));
        await controller.StartAsync();
        controller.ChooseSource(VideoSource.Ps5);
        Assert.False(controller.IsRunning);
        controller.Dispose();
    }

    [Fact]
    public void ClosedWindowStatusIsExplained() =>
        Assert.Equal("Pencere kapandı. Yeniden bir pencere seçin.",
            AppController.StatusText(SessionState.Disconnected(DisconnectKind.WindowClosed), true, Localizer.ForLanguage("tr")));
}
