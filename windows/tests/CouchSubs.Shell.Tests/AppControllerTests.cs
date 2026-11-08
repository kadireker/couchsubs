// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
using CouchSubs.Core.LgTv;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;

namespace CouchSubs.Shell.Tests;

internal sealed class MemoryStore : IAppStore
{
    public Dictionary<string, object?> Values { get; } = [];
    public void Save<T>(string key, T value) => Values[key] = value;
    public T? Load<T>(string key) => Values.TryGetValue(key, out var value) && value is T typed ? typed : default;
    public void Remove(string key) => Values.Remove(key);
}

internal sealed class MemorySettings : ISettingsStore
{
    public UserSettings? Saved { get; private set; }
    public UserSettings Load() => Saved ?? new UserSettings();
    public void Save(UserSettings settings) => Saved = settings;
}

internal sealed class FakeSleep : ISleepGuard
{
    public bool Active { get; private set; }
    public void Begin() => Active = true;
    public void End() => Active = false;
}

internal sealed class FakeStream : IStreamSession
{
    public event Action<Frame>? FrameReceived;
    public event Action<SessionState>? StateChanged;
    public bool Started { get; private set; }
    public void Start() => Started = true;
    public void Stop() => Started = false;
    public void ReconnectNow() { }
    public void Dispose() { }
    public void Emit(SessionState state) => StateChanged?.Invoke(state);
    public void Emit(Frame frame) => FrameReceived?.Invoke(frame);
}

internal sealed class FakeKeeper : IKeeper
{
    public bool Running { get; private set; }
    public void Start(TimeSpan interval) => Running = true;
    public void Stop() => Running = false;
    public void Reconnect() { }
}

internal sealed class FakeTranslation : ITranslationModel
{
    public bool Installed { get; set; } = true;
    public int Downloads { get; private set; }
    public TaskCompletionSource? DownloadGate { get; set; }
    public bool IsSupported(string source, string target) => source == "en" && target == "tr";
    public bool IsInstalled(string source, string target) => Installed;
    public async Task DownloadAsync(string source, string target, IProgress<double> progress, CancellationToken cancellationToken)
    {
        Downloads++;
        progress.Report(0.5);
        if (DownloadGate is not null) await DownloadGate.Task;
        Installed = true;
        progress.Report(1);
    }
    public ITextTranslator Create(string source, string target) => new EchoTranslator();

    private sealed class EchoTranslator : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult("TR: " + text);
    }
}

/// Okuyucu her okumada satırları yeniden sorar; test oyundaki altyazıyı okuma sürerken değiştirebilir.
internal sealed class FakeReader(Func<Reading?> lines) : ITextReader
{
    public string Read(Frame image) => lines()?.Text ?? "";
    public Reading ReadLines(Frame image, bool deep) => lines() ?? Reading.Empty;
}

/// Kullanıcının seçtiği sahte pencere; her başlatmada yeni bir sahte oturum verir.
internal sealed class FakeCaptureTarget(string title) : ICaptureTarget
{
    public string Title { get; } = title;
    public bool IsClosed { get; set; }
    public List<FakeStream> Streams { get; } = [];
    public IStreamSession MakeStream()
    {
        var stream = new FakeStream();
        Streams.Add(stream);
        return stream;
    }
}

internal sealed class FakeServices : IAppServices
{
    /// Pencere seçicide sırayla seçilecek pencereler; boşsa kullanıcı vazgeçmiş sayılır.
    public Queue<FakeCaptureTarget?> Windows { get; } = [];
    public int Picks { get; private set; }
    public Task<ICaptureTarget?> PickWindowAsync()
    {
        Picks++;
        return Task.FromResult<ICaptureTarget?>(Windows.Count > 0 ? Windows.Dequeue() : null);
    }

    public List<ConsoleInfo> Consoles { get; } = [new("192.168.1.253", "PS5-789", true)];
    public List<LgTv> Tvs { get; } = [new("LG TV", "192.168.1.153")];
    public RegisteredConsole? Registered { get; private set; }
    public Exception? RegisterError { get; set; }
    public string? LaunchedServer { get; private set; }
    public List<FakeStream> Streams { get; } = [];
    public List<FakeKeeper> Keepers { get; } = [];
    public List<string> ReaderLanguages { get; } = [];
    /// <summary>Okuyucunun her karede bulduğu satırlar (verilmezse karede yazı yoktur).</summary>
    public Reading? ReaderLines { get; set; }
    public bool HasOcr { get; set; } = true;
    public bool HasOcrPack { get; set; } = true;
    public bool HasOcrLanguage(string language) => HasOcrPack;
    public IDubbingModel? Dubbing { get; set; }
    public IDubbingModel? FastDubbing { get; set; }
    public FakeTranslation Translation { get; } = new();
    ITranslationModel IAppServices.Translation => Translation;
    public OnlineKeyCheck KeyCheck { get; set; } = new(OnlineKeyState.Valid, 10, 500_000);
    public List<string> CheckedKeys { get; } = [];
    public List<(string Key, ITextTranslator? Fallback)> OnlineTranslators { get; } = [];
    public Task<OnlineKeyCheck> CheckOnlineKeyAsync(string key)
    {
        CheckedKeys.Add(key);
        return Task.FromResult(KeyCheck);
    }
    public ITextTranslator? MakeOnlineTranslator(string key, ITextTranslator? fallback)
    {
        OnlineTranslators.Add((key, fallback));
        return fallback ?? Translation.Create("en", "tr");
    }

    public Task<IReadOnlyList<ConsoleInfo>> DiscoverConsolesAsync() => Task.FromResult<IReadOnlyList<ConsoleInfo>>(Consoles);
    public Task<byte[]> AccountIdFromRedirectAsync(Uri redirect) => Task.FromResult(Enumerable.Repeat((byte)1, 8).ToArray());
    public Task<RegisteredConsole> RegisterAsync(string host, byte[] accountId, uint pin)
    {
        if (RegisterError is not null) throw RegisterError;
        Registered = new RegisteredConsole(host, "PS5-789", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray());
        return Task.FromResult(Registered);
    }
    public Task<IReadOnlyList<LgTv>> DiscoverTvsAsync() => Task.FromResult<IReadOnlyList<LgTv>>(Tvs);
    public Task<TvPairing> PairTvAsync(LgTv tv, string? key, string server)
    {
        LaunchedServer = server;
        PairCount++;
        return Task.FromResult(new TvPairing("tv-key", AppOnTv is not null));
    }
    public IStreamSession MakeStream(RegisteredConsole console, byte[] accountId)
    {
        var stream = new FakeStream();
        Streams.Add(stream);
        return stream;
    }
    public IKeeper MakeKeeper(LgTv tv, string? key, IReadOnlyDictionary<string, string> parameters, Func<bool> appConnected, Action<LgTv> onMoved)
    {
        var keeper = new FakeKeeper();
        Keepers.Add(keeper);
        return keeper;
    }
    public string? AppOnTv { get; set; } = "0.6.1";
    public bool DevMode { get; set; }
    public bool KeyServer { get; set; } = true;
    public bool RefuseKey { get; set; }
    public int Installs { get; private set; }
    public int PairCount { get; private set; }
    public string? BundledTvAppVersion { get; set; } = "0.6.1";
    public Task<bool> IsDevModeOnAsync(string host) => Task.FromResult(DevMode);
    public Task<string> FetchDevKeyAsync(string host, string passphrase)
    {
        if (!KeyServer) throw new DevModeException(DevModeError.KeyServerOff);
        if (passphrase != "ABC123") throw new DevModeException(DevModeError.WrongPassphrase);
        return Task.FromResult("dev-key");
    }
    public Task<string?> InstalledTvAppVersionAsync(string host, string devKey)
    {
        if (RefuseKey) throw new DevModeException(DevModeError.KeyRefused);
        return Task.FromResult(AppOnTv);
    }
    public Task InstallTvAppAsync(string host, string devKey)
    {
        Installs++;
        AppOnTv = BundledTvAppVersion;
        return Task.CompletedTask;
    }
    public int? RenewedHours { get; set; } = 999;
    public int Renewals { get; private set; }
    public Task<int?> RenewDevSessionAsync(string host, string devKey)
    {
        Renewals++;
        return Task.FromResult(RenewedHours);
    }

    // Samsung ve Android TV
    public Dictionary<TvBrand, BundledTvApp> Bundled { get; } = [];
    public bool SamsungDevMode { get; set; }
    public List<string> AndroidHosts { get; } = [];
    public DevModeError? InstallError { get; set; }
    public List<TvInstallEvent> Events { get; } = [];
    public List<(TvBrand Brand, string Host)> BrandInstalls { get; } = [];
    public BundledTvApp? BundledApp(TvBrand brand) => Bundled.GetValueOrDefault(brand);
    public Task<bool> IsSamsungDevModeOnAsync(string host) => Task.FromResult(SamsungDevMode);
    public Task InstallSamsungAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent)
    {
        Events.ForEach(onEvent);
        if (InstallError is { } error) throw new DevModeException(error);
        BrandInstalls.Add((TvBrand.Samsung, host));
        return Task.CompletedTask;
    }
    public Task<bool> IsAndroidDebugOnAsync(string host) => Task.FromResult(AndroidHosts.Contains(host));
    public Task<IReadOnlyList<string>> ScanAndroidTvsAsync() => Task.FromResult<IReadOnlyList<string>>([.. AndroidHosts]);
    public Task<string> InstallAndroidAppAsync(string host, BundledTvApp app, Action<TvInstallEvent> onEvent)
    {
        Events.ForEach(onEvent);
        if (InstallError is { } error) throw new DevModeException(error);
        BrandInstalls.Add((TvBrand.AndroidTv, host));
        return Task.FromResult(app.Version);
    }
    public ITextReader? MakeReader(string language)
    {
        ReaderLanguages.Add(language);
        return HasOcr ? new FakeReader(() => ReaderLines) : null;
    }
}

public class AppControllerTests
{
    private static readonly RegisteredConsole Console =
        new("192.168.1.253", "PS5", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray());

    internal static AppController Make(FakeServices? services = null, MemoryStore? store = null, MemorySettings? settings = null, FakeSleep? sleep = null) =>
        new(services ?? new FakeServices(), store ?? new MemoryStore(), settings ?? new MemorySettings(), sleep ?? new FakeSleep(), Localizer.ForLanguage("en"), action => action());

    [Fact]
    public void FreshInstallStartsAtWelcome() => Assert.Equal(OnboardingStep.Welcome, Make().Step);

    [Fact]
    public void CompleteSetupOpensMainScreen()
    {
        var store = new MemoryStore();
        store.Save(StoreKeys.AccountId, Enumerable.Repeat((byte)1, 8).ToArray());
        store.Save(StoreKeys.Console, Console);
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "192.168.1.153"));
        store.Save(StoreKeys.TvKey, "tv-key");
        Assert.Equal(OnboardingStep.Done, Make(store: store).Step);
    }

    [Fact]
    public async Task PsnStepNeedsAccountId()
    {
        var controller = Make();
        controller.Step = OnboardingStep.PsnLogin;
        Assert.False(controller.CanAdvance);
        await controller.CompletePsnLoginAsync(new Uri("https://remoteplay.dl.playstation.net/remoteplay/redirect?code=x"));
        Assert.True(controller.CanAdvance);
    }

    [Fact]
    public async Task ConsoleStepNeedsRegistration()
    {
        var services = new FakeServices();
        var controller = Make(services);
        await controller.CompletePsnLoginAsync(new Uri("https://x/?code=x"));
        controller.Step = OnboardingStep.Console;
        Assert.False(controller.CanAdvance);
        await controller.RegisterAsync("192.168.1.253", "12345678");
        Assert.True(controller.CanAdvance);
        Assert.Equal("192.168.1.253", services.Registered?.Host);
    }

    /// PS5 hesabı tanımazsa kullanıcıya kodu hangi kullanıcıdan alacağı söylenmeli.
    [Fact]
    public async Task WrongAccountRegistrationExplainsWhatToDo()
    {
        var services = new FakeServices { RegisterError = new RegistrationException(RegistrationFailure.WrongAccount) };
        var controller = Make(services);
        await controller.CompletePsnLoginAsync(new Uri("https://x/?code=x"));
        var error = await Assert.ThrowsAsync<AppException>(() => controller.RegisterAsync("192.168.1.253", "12345678"));
        Assert.Equal(AppError.WrongAccount, error.Error);
    }

    [Fact]
    public async Task OtherRegistrationFailureSuggestsNewCode()
    {
        var services = new FakeServices { RegisterError = new RegistrationException(RegistrationFailure.Failed) };
        var controller = Make(services);
        await controller.CompletePsnLoginAsync(new Uri("https://x/?code=x"));
        var error = await Assert.ThrowsAsync<AppException>(() => controller.RegisterAsync("192.168.1.253", "12345678"));
        Assert.Equal(AppError.RegistrationFailed, error.Error);
    }

    [Fact]
    public async Task PinMustBeEightDigits()
    {
        var controller = Make();
        await controller.CompletePsnLoginAsync(new Uri("https://x/?code=x"));
        var error = await Assert.ThrowsAsync<AppException>(() => controller.RegisterAsync("192.168.1.253", "1234"));
        Assert.Equal(AppError.InvalidPin, error.Error);
        Assert.Null(controller.Console);
    }

    [Fact]
    public async Task PairingTvPassesServerAddressAndStoresKey()
    {
        var services = new FakeServices();
        var store = new MemoryStore();
        var controller = Make(services, store);
        await controller.PairAsync(new LgTv("LG TV", "192.168.1.153"));
        Assert.StartsWith("http://", services.LaunchedServer);
        Assert.EndsWith($":{controller.ServerPort}", services.LaunchedServer);
        Assert.Equal("tv-key", store.Load<string>(StoreKeys.TvKey));
        controller.Dispose();
    }

    /// Çeviri modeli (~534 MB) sihirbazın başında arka planda inmeye başlar; model adımına gelindiğinde çoğunlukla hazırdır.
    [Fact]
    public void ModelDownloadStartsInBackgroundAfterWelcome()
    {
        var services = new FakeServices();
        services.Translation.Installed = false;
        services.Translation.DownloadGate = new TaskCompletionSource();
        var controller = Make(services);
        Assert.Equal(0, services.Translation.Downloads);
        controller.Advance();
        Assert.Equal(1, services.Translation.Downloads);
        Assert.True(controller.ModelDownloading);
        services.Translation.DownloadGate.SetResult();
    }

    [Fact]
    public async Task ModelStepWaitsForDownload()
    {
        var services = new FakeServices();
        services.Translation.Installed = false;
        services.Translation.DownloadGate = new TaskCompletionSource();
        var controller = Make(services);
        controller.Advance();
        controller.Step = OnboardingStep.Translation;
        Assert.False(controller.CanAdvance);
        services.Translation.DownloadGate.SetResult();
        await controller.WaitForModelAsync();
        Assert.True(controller.CanAdvance);
        Assert.Equal(1.0, controller.ModelProgress);
    }

    [Fact]
    public async Task OnboardingWalksAllStepsInOrder()
    {
        var controller = Make();
        Assert.Equal(OnboardingStep.Welcome, controller.Step); controller.Advance();
        Assert.Equal(OnboardingStep.SecondAccount, controller.Step); controller.Advance();
        Assert.Equal(OnboardingStep.PsnLogin, controller.Step);
        await controller.CompletePsnLoginAsync(new Uri("https://remoteplay.dl.playstation.net/remoteplay/redirect?code=x"));
        controller.Advance();
        Assert.Equal(OnboardingStep.Console, controller.Step);
        await controller.RegisterAsync("192.168.1.253", "12345678"); controller.Advance();
        Assert.Equal(OnboardingStep.Tv, controller.Step);
        await controller.PairAsync(new LgTv("LG TV", "192.168.1.153")); controller.Advance();
        Assert.Equal(OnboardingStep.Translation, controller.Step); controller.Advance();
        Assert.Equal(OnboardingStep.Test, controller.Step); controller.Advance();
        Assert.Equal(OnboardingStep.Done, controller.Step);
        controller.Dispose();
    }
}

public class AppControllerRunTests
{
    private static (AppController Controller, FakeServices Services, FakeSleep Sleep) Ready(FakeServices? services = null)
    {
        services ??= new FakeServices();
        var store = new MemoryStore();
        store.Save(StoreKeys.AccountId, Enumerable.Repeat((byte)1, 8).ToArray());
        store.Save(StoreKeys.Console, new RegisteredConsole("192.168.1.253", "PS5", Enumerable.Repeat((byte)0x31, 16).ToArray(), Enumerable.Repeat((byte)2, 16).ToArray()));
        store.Save(StoreKeys.Tv, new LgTv("LG TV", "127.0.0.1"));
        store.Save(StoreKeys.TvKey, "tv-key");
        var sleep = new FakeSleep();
        return (AppControllerTests.Make(services, store, sleep: sleep), services, sleep);
    }

    [Fact]
    public async Task DoubleStartCreatesOneSession()
    {
        var (controller, services, _) = Ready();
        await Task.WhenAll(controller.StartAsync(), controller.StartAsync());
        Assert.Single(services.Streams);
        Assert.Single(services.Keepers);
        Assert.True(controller.IsRunning);
        controller.Dispose();
    }

    [Fact]
    public async Task RunningPreventsIdleSleep()
    {
        var (controller, _, sleep) = Ready();
        await controller.StartAsync();
        Assert.True(sleep.Active);
        controller.Stop();
        Assert.False(sleep.Active);
        controller.Dispose();
    }

    [Fact]
    public async Task DisconnectClearsSubtitleOnTv()
    {
        var (controller, services, _) = Ready();
        await controller.StartAsync();
        using var http = new HttpClient();
        using var response = await http.GetAsync($"http://127.0.0.1:{controller.ServerPort}/events", HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync());
        await Task.Delay(200);
        services.Streams[0].Emit(SessionState.Disconnected(DisconnectKind.Network));
        var sawClear = false;
        while (await reader.ReadLineAsync() is { } line)
            if (line == "event: clear") { sawClear = true; break; }
        Assert.True(sawClear);
        controller.Dispose();
    }

    [Fact]
    public void SettingsArePersistedOutsideTheSecureStore()
    {
        var store = new MemoryStore();
        var settings = new MemorySettings();
        var controller = AppControllerTests.Make(store: store, settings: settings);
        controller.Settings = controller.Settings with { Style = new SubtitleStyle(58, 7) };
        Assert.Empty(store.Values);
        Assert.Equal(58, AppControllerTests.Make(store: store, settings: settings).Settings.Style.FontSize);
    }

    [Fact]
    public async Task ChangingLanguageWhileRunningRebuildsPipeline()
    {
        var (controller, services, _) = Ready();
        await controller.StartAsync();
        Assert.Equal("en", controller.ActiveSourceLanguage);
        controller.Settings = controller.Settings with { SourceLanguage = "ja" };
        Assert.Equal("ja", controller.ActiveSourceLanguage);
        Assert.Equal(["en", "ja"], services.ReaderLanguages);
        controller.Dispose();
    }

    /// Oyun dilinin paketi yokken Windows sistemin diliyle okur; okuma sürse de ana ekran uyarır.
    [Fact]
    public async Task OcrFallingBackToSystemLanguageIsReported()
    {
        var (controller, _, _) = Ready(new FakeServices { HasOcrPack = false });
        await controller.StartAsync();
        Assert.True(controller.OcrMissing);
        controller.Dispose();
    }

    /// Hızlı ses (Windows'un Türkçe sesi) indirme beklemez; doğal ses indirilmemişken de dublaj hazırdır.
    [Fact]
    public void FastVoiceNeedsNoDownload()
    {
        var services = new FakeServices { Dubbing = new FakeDubbingModel(installed: false), FastDubbing = new FakeDubbingModel(installed: true) };
        var (controller, _, _) = Ready(services);
        Assert.True(controller.DubbingAvailable);
        Assert.False(controller.DubbingInstalled);
        controller.Settings = controller.Settings with { Dubbing = controller.Settings.Dubbing with { Engine = DubbingEngine.Fast } };
        Assert.True(controller.DubbingInstalled);
        controller.Dispose();
    }

    /// DeepL anahtarı yalnız denenip kabul edilirse saklanır; reddedilen anahtar ne saklanır ne çeviriyi açar.
    [Fact]
    public async Task RejectedDeepLKeyIsNotSaved()
    {
        var services = new FakeServices { KeyCheck = new OnlineKeyCheck(OnlineKeyState.Invalid) };
        var (controller, _, _) = Ready(services);
        var check = await controller.SetDeepLKeyAsync("  wrong  ");
        Assert.Equal(OnlineKeyState.Invalid, check.State);
        Assert.Equal(["wrong"], services.CheckedKeys);
        Assert.False(controller.HasDeepLKey);
        Assert.False(controller.Settings.OnlineTranslation);
        controller.Dispose();
    }

    /// Geçerli anahtarla çeviri DeepL'e geçer; cihaz üstü model inmemiş olsa da çeviri hazır sayılır.
    [Fact]
    public async Task AcceptedDeepLKeyTurnsOnlineTranslationOn()
    {
        var services = new FakeServices();
        services.Translation.Installed = false;
        var (controller, _, _) = Ready(services);
        await controller.SetDeepLKeyAsync("key:fx");
        Assert.True(controller.HasDeepLKey);
        Assert.True(controller.Settings.OnlineTranslation);
        Assert.Equal(TranslatorStatus.Ready, controller.TranslatorStatus);

        await controller.StartAsync();
        Assert.Equal("key:fx", Assert.Single(services.OnlineTranslators).Key);
        Assert.Null(services.OnlineTranslators[0].Fallback);

        controller.RemoveDeepLKey();
        Assert.False(controller.HasDeepLKey);
        Assert.False(controller.Settings.OnlineTranslation);
        Assert.Equal(TranslatorStatus.NeedsDownload, controller.TranslatorStatus);
        controller.Dispose();
    }

    /// Windows'ta oyun dilinin OCR paketi yoksa ana ekran bunu söyler.
    [Fact]
    public async Task MissingOcrLanguageIsReported()
    {
        var (controller, _, _) = Ready(new FakeServices { HasOcr = false });
        await controller.StartAsync();
        Assert.True(controller.OcrMissing);
        controller.Dispose();
    }

    [Fact]
    public void LoginPasscodeIsExplained()
    {
        var text = Localizer.ForLanguage("en");
        Assert.Equal("The second user has a login passcode on the PS5. Turn it off in Settings › Users and Accounts.",
            AppController.StatusText(SessionState.Disconnected(DisconnectKind.LoginPasscode), true, text));
    }

    [Fact]
    public void DisconnectReasonsAreExplained()
    {
        var text = Localizer.ForLanguage("en");
        Assert.Equal("Turn on your PS5", AppController.StatusText(SessionState.Disconnected(DisconnectKind.ConsoleAsleep), true, text));
        Assert.Equal("Remote Play is in use on another device", AppController.StatusText(SessionState.Disconnected(DisconnectKind.InUse), true, text));
        Assert.Equal("The PS5 refused the connection. Set up the PS5 again.", AppController.StatusText(SessionState.Disconnected(DisconnectKind.Refused), true, text));
        Assert.Equal("Reconnecting…", AppController.StatusText(SessionState.Disconnected(DisconnectKind.Network), true, text));
        Assert.Equal("Not connected", AppController.StatusText(SessionState.Disconnected(DisconnectKind.Stopped), false, text));
    }

    [Fact]
    public void TurkishStatusTextComesFromSharedCatalog() =>
        Assert.Equal("Bağlı", AppController.StatusText(SessionState.Streaming, true, Localizer.ForLanguage("tr")));
}

public class StatusToneTests
{
    /// Çeviri başlamamışken "bağlı değil" bir uyarı değildir; nokta gri kalır.
    [Fact]
    public void IdleWhenNotRunning()
    {
        Assert.Equal(StatusTone.Idle, AppController.Ps5ToneFor(SessionState.Disconnected(DisconnectKind.Stopped), running: false));
        Assert.Equal(StatusTone.Idle, AppController.TvToneFor(clients: 0, running: false));
    }

    [Fact]
    public void WarningWhileRunningButNotConnected()
    {
        Assert.Equal(StatusTone.Warning, AppController.Ps5ToneFor(SessionState.Disconnected(DisconnectKind.Network), running: true));
        Assert.Equal(StatusTone.Warning, AppController.Ps5ToneFor(SessionState.Connecting, running: true));
        Assert.Equal(StatusTone.Warning, AppController.TvToneFor(clients: 0, running: true));
    }

    [Fact]
    public void GoodWhenConnected()
    {
        Assert.Equal(StatusTone.Good, AppController.Ps5ToneFor(SessionState.Streaming, running: true));
        Assert.Equal(StatusTone.Good, AppController.TvToneFor(clients: 1, running: false));
    }
}

public class TvAppInstallTests
{
    private static readonly LgTv Tv = new("LG TV", "192.168.1.153");

    private static AppController Make(FakeServices services, MemoryStore? store = null) =>
        new(services, store ?? new MemoryStore(), new MemorySettings(), new FakeSleep(), Localizer.ForLanguage("en"), action => action());

    [Fact]
    public async Task MissingAppWithDevModeOffShowsTheSteps()
    {
        var services = new FakeServices { AppOnTv = null };
        var controller = Make(services);
        await controller.PairAsync(Tv);
        Assert.Equal(TvAppKind.DevModeOff, controller.TvApp.Kind);
        Assert.NotNull(controller.Tv);
    }

    [Fact]
    public async Task PassphraseInstallsAndOpensTheApp()
    {
        var services = new FakeServices { AppOnTv = null, DevMode = true };
        var store = new MemoryStore();
        var controller = Make(services, store);
        await controller.PairAsync(Tv);
        Assert.Equal(TvAppKind.NeedsPassphrase, controller.TvApp.Kind);
        var pairs = services.PairCount;
        await controller.InstallTvAppAsync(" ABC123 ");
        Assert.Equal(new TvAppState(TvAppKind.UpToDate, "0.6.1"), controller.TvApp);
        Assert.Equal(1, services.Installs);
        Assert.Equal("dev-key", store.Load<string>(StoreKeys.TvDevKey));
        Assert.Equal(pairs + 1, services.PairCount);
    }

    [Fact]
    public async Task WrongPassphraseAndClosedKeyServerAreExplained()
    {
        var services = new FakeServices { AppOnTv = null, DevMode = true };
        var controller = Make(services);
        await controller.PairAsync(Tv);
        await controller.InstallTvAppAsync("WRONG");
        Assert.Equal(DevModeError.WrongPassphrase, controller.TvApp.Error);
        services.KeyServer = false;
        await controller.InstallTvAppAsync("ABC123");
        Assert.Equal(DevModeError.KeyServerOff, controller.TvApp.Error);
        Assert.Equal(0, services.Installs);
    }

    [Fact]
    public async Task OlderAppIsUpdatedWithoutAsking()
    {
        var services = new FakeServices { AppOnTv = "0.4.0", DevMode = true };
        var store = new MemoryStore();
        store.Save(StoreKeys.TvDevKey, "dev-key");
        var controller = Make(services, store);
        await controller.PairAsync(Tv);
        Assert.Equal(1, services.Installs);
        Assert.Equal(TvAppKind.UpToDate, controller.TvApp.Kind);
    }

    [Fact]
    public async Task CurrentAppIsNotReinstalled()
    {
        var services = new FakeServices { DevMode = true };
        var store = new MemoryStore();
        store.Save(StoreKeys.TvDevKey, "dev-key");
        var controller = Make(services, store);
        await controller.PairAsync(Tv);
        Assert.Equal(0, services.Installs);
    }

    [Fact]
    public async Task RefusedKeyIsForgottenAndPassphraseAskedAgain()
    {
        var services = new FakeServices { DevMode = true, RefuseKey = true };
        var store = new MemoryStore();
        store.Save(StoreKeys.TvDevKey, "old-key");
        var controller = Make(services, store);
        await controller.PairAsync(Tv);
        Assert.Equal(TvAppKind.NeedsPassphrase, controller.TvApp.Kind);
        Assert.Null(store.Load<string>(StoreKeys.TvDevKey));
    }

    [Fact]
    public async Task LgDeveloperModeIsExtendedOncePerDay()
    {
        var services = new FakeServices { DevMode = true };
        var store = new MemoryStore();
        store.Save(StoreKeys.TvDevKey, "dev-key");
        var controller = Make(services, store);
        await controller.PairAsync(Tv);
        Assert.Equal(999, controller.DevModeHoursLeft);
        await controller.RefreshTvAppAsync();
        Assert.Equal(1, services.Renewals);
    }

    private static readonly LgTv Samsung = new("Samsung", "192.168.1.20", TvBrand.Samsung);
    private static readonly BundledTvApp SamsungApp = new("couchsubs-samsung_0.7.0.wgt", "0.7.0");

    [Fact]
    public async Task SamsungWithDevModeOffShowsTheSteps()
    {
        var services = new FakeServices();
        services.Bundled[TvBrand.Samsung] = SamsungApp;
        var controller = Make(services);
        await controller.PairAsync(Samsung);
        Assert.Equal(TvAppKind.DevModeOff, controller.TvApp.Kind);
        Assert.True(controller.CanInstallTvApp);
    }

    [Fact]
    public async Task SamsungInstallOpensSignInAndRemembersTheVersion()
    {
        var page = new Uri("https://account.samsung.com/signin");
        var services = new FakeServices { SamsungDevMode = true };
        services.Bundled[TvBrand.Samsung] = SamsungApp;
        services.Events.Add(new TvInstallEvent(TvInstallEventKind.SignIn, page));
        var store = new MemoryStore();
        var controller = Make(services, store);
        var opened = new List<Uri>();
        controller.OpenInBrowser = opened.Add;
        await controller.PairAsync(Samsung);
        Assert.Equal([page], opened);
        Assert.Equal(new TvAppState(TvAppKind.UpToDate, "0.7.0"), controller.TvApp);
        Assert.Equal("0.7.0", store.Load<string>(StoreKeys.TvAppVersion));
        await controller.RefreshTvAppAsync();
        Assert.Single(services.BrandInstalls);
    }

    [Fact]
    public async Task SamsungFailureIsShown()
    {
        var services = new FakeServices { SamsungDevMode = true, InstallError = DevModeError.SignInFailed };
        services.Bundled[TvBrand.Samsung] = SamsungApp;
        var controller = Make(services);
        await controller.PairAsync(Samsung);
        Assert.Equal(new TvAppState(TvAppKind.Failed, Error: DevModeError.SignInFailed), controller.TvApp);
    }

    [Fact]
    public async Task AndroidTvIsFoundOnTheNetworkAndRemembered()
    {
        var services = new FakeServices();
        services.Bundled[TvBrand.AndroidTv] = new BundledTvApp("couchsubs-androidtv_0.7.0.apk", "0.7.0");
        var store = new MemoryStore();
        var controller = Make(services, store);
        await controller.PairAsync(new LgTv("Android TV", "", TvBrand.AndroidTv));
        Assert.Equal(TvAppKind.DevModeOff, controller.TvApp.Kind);

        services.AndroidHosts.Add("192.168.1.40");
        services.Events.Add(new TvInstallEvent(TvInstallEventKind.ApproveOnTv));
        await controller.RefreshTvAppAsync();
        Assert.Equal(new TvAppState(TvAppKind.UpToDate, "0.7.0"), controller.TvApp);
        Assert.Equal("192.168.1.40", controller.Tv?.Host);
        Assert.Equal("192.168.1.40", store.Load<LgTv>(StoreKeys.Tv)?.Host);
    }

    [Fact]
    public async Task WorkingAppWithoutKeyAsksNothing()
    {
        var controller = Make(new FakeServices { DevMode = true });
        await controller.PairAsync(Tv);
        Assert.Equal(TvAppKind.Unknown, controller.TvApp.Kind);
    }

    [Fact]
    public async Task OtherBrandsAreNotInstalledThroughLgDeveloperMode()
    {
        var services = new FakeServices { AppOnTv = null, DevMode = true };
        var controller = Make(services);
        await controller.PairAsync(new LgTv("Samsung", "192.168.1.20", TvBrand.Samsung));
        Assert.Equal(TvAppKind.Unknown, controller.TvApp.Kind);
        Assert.Equal(0, services.Installs);
    }

    [Fact]
    public void MovedTvIsSaved()
    {
        var store = new MemoryStore();
        var controller = Make(new FakeServices(), store);
        var moved = new LgTv("LG TV", "192.168.1.230");
        controller.TvMoved(moved);
        Assert.Equal(moved, controller.Tv);
        Assert.Equal(moved, store.Load<LgTv>(StoreKeys.Tv));
    }
}

internal sealed class FakeDubbingModel(bool installed) : IDubbingModel
{
    public bool IsInstalled => installed;
    public long DownloadBytes => 0;
    public Task DownloadAsync(IProgress<double> progress, CancellationToken cancellationToken) => Task.CompletedTask;
    public byte[] VoiceFile(DubbingVoice voice) => [];
    public ISpeechSynthesizer CreateSynthesizer() => throw new NotSupportedException();
}
