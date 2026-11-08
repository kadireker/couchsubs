// SPDX-License-Identifier: AGPL-3.0-only

using System.ComponentModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using CouchSubs.Core;
using CouchSubs.Core.Dubbing;
using CouchSubs.Core.LgTv;
using CouchSubs.RemotePlay;

namespace CouchSubs.Shell;

/// <summary>
/// Uygulamanın durumu ve akışı (Mac'teki AppModel'in karşılığı): kurulum sihirbazı, çeviriyi
/// başlatma/durdurma, PS5 ve TV durumları. Arayüz bu nesneye bağlanır; olaylar <c>post</c> ile
/// arayüz iş parçacığına aktarılır.
/// </summary>
public sealed class AppController : INotifyPropertyChanged, IDisposable
{
    public const string TvAppId = "com.couchsubs.tv";
    public const int DefaultServerPort = 8787;

    private readonly IAppServices services;
    private readonly IAppStore store;
    private readonly ISettingsStore settingsStore;
    private readonly ISleepGuard sleep;
    private readonly Localizer text;
    private readonly Action<Action> post;

    private OnboardingStep step;
    private byte[]? accountId;
    private RegisteredConsole? console;
    private LgTv? tv;
    private TvAppState tvApp = TvAppState.Unknown;
    /// <summary>Son eşleşmede TV uygulaması açılamadı.</summary>
    private bool tvAppMissing;
    /// <summary>TV uygulaması denetimi sürüyor (bekleme ekranı ve çeviri başlangıcı aynı anda çağırabilir).</summary>
    private bool refreshingTvApp;
    private int? devModeHoursLeft;
    private UserSettings settings;
    private bool isRunning;
    private bool isStarting;
    private SessionState sessionState = SessionState.Disconnected(DisconnectKind.Stopped);
    private int tvClients;
    private string lastSource = "";
    private string lastTranslation = "";
    private TranslatorStatus translatorStatus = TranslatorStatus.Ready;
    private bool ocrMissing;
    private Frame? lastFrame;
    private bool modelDownloading;
    private double modelProgress;
    private string? modelError;
    private Task? modelDownload;

    private SubtitleServer server = new(DefaultServerPort);
    private bool serverStarted;
    private IStreamSession? session;
    /// <summary>Pencere kaynağında seçilen pencere; uygulama açık kaldıkça hatırlanır, kapanınca yeniden sorulur.</summary>
    private ICaptureTarget? captureTarget;
    private bool choosingWindow;
    private IKeeper? keeper;
    private SubtitlePipeline? pipeline;
    /// <summary>Hattın yazı okuyucusu; ek bölgelerin tek seferlik okuması da bunu kullanır.</summary>
    private ITextReader? activeReader;
    private int extraBusy;
    private bool subtitlesPaused;
    /// <summary>Oyundaki altyazının son görüldüğü yer; deneme altyazısı TV'de oraya yerleşir.</summary>
    private TextBox? lastSubtitleBox;
    private int processing;
    private Dubber? dubber;
    private string? updateVersion;
    private bool updating;
    private double updateProgress;
    private string? updateError;
    private bool dubbingDownloading;
    private double dubbingProgress;
    private string? dubbingError;

    public event PropertyChangedEventHandler? PropertyChanged;

    public AppController(IAppServices services, IAppStore store, ISettingsStore settingsStore, ISleepGuard sleep, Localizer text, Action<Action> post)
    {
        this.services = services;
        this.store = store;
        this.settingsStore = settingsStore;
        this.sleep = sleep;
        this.text = text;
        this.post = post;
        accountId = store.Load<byte[]>(StoreKeys.AccountId);
        console = store.Load<RegisteredConsole>(StoreKeys.Console);
        tv = store.Load<LgTv>(StoreKeys.Tv);
        settings = settingsStore.Load();
        // Pencere kaynağında PS5, hesap ve TV gerekmez.
        step = Ps5Ready || settings.Source == VideoSource.Window ? OnboardingStep.Done : OnboardingStep.Welcome;
        server.ClientsChanged += OnClientsChanged;
    }

    // --- Durum ---

    public OnboardingStep Step { get => step; set => Set(ref step, value, [nameof(CanAdvance), nameof(StepNumber)]); }
    public static int StepCount => (int)OnboardingStep.Done;
    public int StepNumber => Math.Min((int)step + 1, StepCount);
    public bool HasAccount => accountId is not null;
    public RegisteredConsole? Console => console;
    public LgTv? Tv => tv;
    public bool IsRunning { get => isRunning; private set => Set(ref isRunning, value, [nameof(Ps5StatusText), nameof(Ps5Tone), nameof(TvTone)]); }
    public bool IsStarting { get => isStarting; private set => Set(ref isStarting, value); }
    public SessionState SessionState { get => sessionState; private set => Set(ref sessionState, value, [nameof(Ps5StatusText), nameof(Ps5Tone)]); }
    /// <summary>Görüntü kaynağının (PS5 ya da pencere) durum metni.</summary>
    public string Ps5StatusText => IsWindowSource ? WindowStatusText() : StatusText(sessionState, isRunning, text);
    public StatusTone Ps5Tone => Ps5ToneFor(sessionState, isRunning);
    /// <summary>Görüntü bu bilgisayardaki bir pencereden geliyor.</summary>
    public bool IsWindowSource => settings.Source == VideoSource.Window;
    /// <summary>Durum satırında kaynağın adı: "PS5" ya da "Pencere".</summary>
    public string SourceTitle => IsWindowSource ? text["Window"] : "PS5";
    /// <summary>Seçili pencerenin başlığı; seçilmediyse null.</summary>
    public string? CaptureTitle => captureTarget?.Title;
    /// <summary>PS5 kaynağında ya da pencere kaynağında TV kuruluysa TV durumu gösterilir.</summary>
    public bool ShowsTvStatus => !IsWindowSource || tv is not null;
    /// <summary>PS5 için gereken her şey (hesap, kayıtlı PS5, eşleşmiş TV) hazır.</summary>
    public bool Ps5Ready => accountId is not null && console is not null && tv is not null && store.Load<string>(StoreKeys.TvKey) is not null;
    /// <summary>Altyazı bilgisayardaki katmanda da gösterilir (pencere kaynağı ve ayar açık).</summary>
    public bool ShowsSubtitlesOnPc => IsWindowSource && settings.ShowOnPc;
    public int TvClients { get => tvClients; private set => Set(ref tvClients, value, [nameof(TvTone), nameof(TvStatusText)]); }
    public StatusTone TvTone => TvToneFor(tvClients, isRunning);
    public string TvStatusText => tvClients > 0 ? text["Connected"] : text["Waiting for the TV app"];
    public string LastSource { get => lastSource; private set => Set(ref lastSource, value); }
    public string LastTranslation { get => lastTranslation; private set => Set(ref lastTranslation, value); }
    public TranslatorStatus TranslatorStatus { get => translatorStatus; private set => Set(ref translatorStatus, value); }
    /// <summary>Bilgisayarda yazı tanıma dili yok; ayarlardan eklenmesi gerekir.</summary>
    public bool OcrMissing { get => ocrMissing; private set => Set(ref ocrMissing, value); }
    /// <summary>Canlı altyazı çevirisi duraklatıldı (Num 0); ek bölge ve serbest alan çevirileri çalışmaya devam eder.</summary>
    public bool SubtitlesPaused { get => subtitlesPaused; private set => Set(ref subtitlesPaused, value); }
    public Frame? LastFrame { get => lastFrame; private set => Set(ref lastFrame, value); }
    public string? ActiveSourceLanguage { get; private set; }
    /// <summary>Sunucunun gerçekten dinlediği port (8787 doluysa rastgele bir port).</summary>
    public int ServerPort { get; private set; } = DefaultServerPort;

    /// <summary>Bu oyun dilinden hedef dile çeviri modeli var mı (ayarlarda listelenecek diller).</summary>
    public bool SupportsLanguage(string source) => services.Translation.IsSupported(source, settings.TargetLanguage);

    public bool ModelInstalled => services.Translation.IsInstalled(settings.SourceLanguage, settings.TargetLanguage);
    public bool ModelDownloading { get => modelDownloading; private set => Set(ref modelDownloading, value); }
    public double ModelProgress { get => modelProgress; private set => Set(ref modelProgress, value); }
    public string? ModelError { get => modelError; private set => Set(ref modelError, value); }

    public UserSettings Settings
    {
        get => settings;
        set
        {
            var old = settings;
            if (!Set(ref settings, value)) return;
            settingsStore.Save(value);
            server.SendStyle(value.Style);
            if (old.Source != value.Source)
            {
                // Kaynak değişince süren çeviri durur; yeni kaynakla yeniden başlatılır.
                if (isRunning) Stop();
                Changed(nameof(IsWindowSource), nameof(SourceTitle), nameof(Ps5StatusText), nameof(ShowsTvStatus), nameof(ShowsSubtitlesOnPc));
            }
            if (old.ShowOnPc != value.ShowOnPc) Changed(nameof(ShowsSubtitlesOnPc));
            // Katman kapatıldıysa ekrandaki altyazı da kalkar.
            if (old is { Source: VideoSource.Window, ShowOnPc: true } && !ShowsSubtitlesOnPc) post(() => PcSubtitleChanged?.Invoke(null));
            if (old.Dubbing != value.Dubbing) DubbingSettingsChanged(old.Dubbing);
            if (isRunning && (old.SourceLanguage != value.SourceLanguage || old.TargetLanguage != value.TargetLanguage
                              || old.OnlineTranslation != value.OnlineTranslation))
            {
                RefreshTranslatorStatus();
                MakePipeline();
            }
        }
    }

    // --- Sihirbaz ---

    public bool CanAdvance => step switch
    {
        OnboardingStep.Welcome or OnboardingStep.SecondAccount or OnboardingStep.Test => true,
        OnboardingStep.PsnLogin => accountId is not null,
        OnboardingStep.Console => console is not null,
        // Pencere kaynağında TV isteğe bağlıdır (monitörde oynayan TV'siz devam eder).
        OnboardingStep.Tv => IsWindowSource || (tv is not null && store.Load<string>(StoreKeys.TvKey) is not null),
        OnboardingStep.Translation => ModelInstalled,
        _ => false,
    };

    public void Advance()
    {
        if (!CanAdvance || step == OnboardingStep.Done) return;
        if (step == OnboardingStep.Welcome)
        {
            // Model büyük; kullanıcı diğer adımlarla uğraşırken arka planda iner.
            StartModelDownload();
            // Hoş geldin ekranında Devam, PS5 kurulumudur (pencere için ayrı düğme var).
            if (IsWindowSource) Settings = settings with { Source = VideoSource.Ps5 };
        }
        // Pencere kaynağında TV yoksa deneme altyazısını gösterecek TV de yoktur.
        Step = IsWindowSource && step == OnboardingStep.Translation && tv is null ? OnboardingStep.Done : step + 1;
    }

    public void Back()
    {
        // Pencere kaynağında PS5 adımları atlanmıştı; TV adımından hoş geldin ekranına dönülür.
        if (IsWindowSource && step == OnboardingStep.Tv) Step = OnboardingStep.Welcome;
        else if (step > OnboardingStep.Welcome) Step = step - 1;
    }

    /// <summary>
    /// Hoş geldin ekranındaki "Bu bilgisayardaki bir pencereyi kullan": PSN, PS5 ve TV kurulumu atlanır; TV adımı
    /// isteğe bağlı olarak gösterilir (Xbox TV'ye bağlıysa TV de kurulabilir).
    /// </summary>
    public void UseWindowSource()
    {
        StartModelDownload();
        Settings = settings with { Source = VideoSource.Window, ShowOnPc = true };
        Step = OnboardingStep.Tv;
    }

    /// <summary>
    /// Ayarlardaki görüntü kaynağı seçimi. PS5'e geçilip PS5 henüz kurulmadıysa kurulum sihirbazı açılır.
    /// </summary>
    public void ChooseSource(VideoSource source)
    {
        if (settings.Source == source) return;
        Settings = settings with { Source = source };
        if (source == VideoSource.Ps5 && !Ps5Ready) Step = OnboardingStep.Welcome;
    }

    public async Task CompletePsnLoginAsync(Uri redirect)
    {
        var id = await services.AccountIdFromRedirectAsync(redirect);
        accountId = id;
        store.Save(StoreKeys.AccountId, id);
        Changed(nameof(HasAccount), nameof(CanAdvance));
    }

    public Task<IReadOnlyList<ConsoleInfo>> DiscoverConsolesAsync() => services.DiscoverConsolesAsync();

    public async Task RegisterAsync(string host, string pin)
    {
        var digits = new string([.. pin.Where(char.IsDigit)]);
        if (digits.Length != 8 || !uint.TryParse(digits, out var value)) throw Error(AppError.InvalidPin);
        if (accountId is null) throw Error(AppError.MissingAccount);
        RegisteredConsole registered;
        try
        {
            registered = await services.RegisterAsync(host, accountId, value);
        }
        catch (RegistrationException error)
        {
            throw Error(error.Failure == RegistrationFailure.WrongAccount ? AppError.WrongAccount : AppError.RegistrationFailed);
        }
        console = registered;
        store.Save(StoreKeys.Console, registered);
        Changed(nameof(Console), nameof(CanAdvance));
    }

    public Task<IReadOnlyList<LgTv>> DiscoverTvsAsync() => services.DiscoverTvsAsync();

    public async Task PairAsync(LgTv target)
    {
        await StartServerAsync();
        var pairing = await services.PairTvAsync(target, store.Load<string>(StoreKeys.TvKey), ServerAddress(target));
        if (tv?.Brand != target.Brand || (target.Brand != TvBrand.AndroidTv && tv?.Host != target.Host))
            store.Remove(StoreKeys.TvAppVersion);
        tv = target;
        store.Save(StoreKeys.Tv, target);
        store.Save(StoreKeys.TvKey, pairing.Key);
        tvAppMissing = !pairing.Launched;
        Changed(nameof(Tv), nameof(CanAdvance), nameof(CanInstallTvApp), nameof(ShowsTvStatus));
        if (target.Brand == TvBrand.Lg) await RefreshTvAppAsync();
        // Samsung girişi ya da Android onayı dakikalar sürebilir; eşleşme beklemesin.
        else _ = RefreshTvAppAsync();
    }

    // --- TV uygulamasını kurma ---

    public TvAppState TvApp
    {
        get => tvApp;
        private set
        {
            tvApp = value;
            Changed(nameof(TvApp));
        }
    }

    /// <summary>LG geliştirici modunun kalan süresi (saat); CouchSubs uzattıktan sonra bilinir.</summary>
    public int? DevModeHoursLeft
    {
        get => devModeHoursLeft;
        private set
        {
            devModeHoursLeft = value;
            Changed(nameof(DevModeHoursLeft));
        }
    }

    /// <summary>Seçili TV'ye uygulamayı CouchSubs kurabilir mi (paket uygulamayla geldi mi)?</summary>
    public bool CanInstallTvApp => tv is { } target &&
        (target.Brand == TvBrand.Lg ? services.BundledTvAppVersion is not null : services.BundledApp(target.Brand) is not null);

    /// <summary>Samsung'un sertifika sayfasını tarayıcıda açar (arayüz atar; testlerde boş kalır).</summary>
    public Action<Uri> OpenInBrowser { get; set; } = _ => { };

    /// <summary>Samsung'da geliştirici modunda "Host PC IP" olarak yazılacak bu bilgisayarın adresi.</summary>
    public string? ComputerAddress => LocalAddress(tv is { Host.Length: > 0 } target ? target.Host : "192.168.1.1");

    /// <summary>
    /// Geliştirici modu açık TV'de uygulamayı eksikse kurar, eskiyse günceller.
    /// Anahtar yoksa ve uygulama çalışıyorsa kullanıcıdan bir şey istenmez.
    /// </summary>
    public async Task RefreshTvAppAsync()
    {
        if (tv is not { } target || refreshingTvApp) return;
        refreshingTvApp = true;
        try
        {
            switch (target.Brand)
            {
                case TvBrand.Lg: await RefreshLgAppAsync(target); break;
                case TvBrand.Samsung: await RefreshSamsungAppAsync(target); break;
                case TvBrand.AndroidTv: await RefreshAndroidAppAsync(target); break;
            }
        }
        finally
        {
            refreshingTvApp = false;
        }
    }

    private string? InstalledAppVersion => store.Load<string>(StoreKeys.TvAppVersion);

    private bool NeedsInstall(BundledTvApp bundled) =>
        InstalledAppVersion is not { } installed || DevModeInstaller.IsNewer(bundled.Version, installed);

    private async Task RefreshLgAppAsync(LgTv target)
    {
        if (services.BundledTvAppVersion is not { } bundled) return;
        if (!await services.IsDevModeOnAsync(target.Host))
        {
            TvApp = tvAppMissing ? new TvAppState(TvAppKind.DevModeOff) : TvAppState.Unknown;
            return;
        }
        if (store.Load<string>(StoreKeys.TvDevKey) is not { } devKey)
        {
            TvApp = tvAppMissing ? new TvAppState(TvAppKind.NeedsPassphrase) : TvAppState.Unknown;
            return;
        }
        await InstallTvAppAsync(target, devKey, bundled);
    }

    /// <summary>Kurulum hatasında TV'nin verdiği sebep de gösterilir; genel mesaj tek başına sorunu bulmaya yetmiyordu.</summary>
    private static TvAppState FailedState(DevModeException error) =>
        new(TvAppKind.Failed, Error: error.Error,
            Reason: error.Error == DevModeError.InstallFailed && !string.IsNullOrWhiteSpace(error.Detail) ? error.Detail.Trim() : null);

    /// <summary>Samsung: geliştirici modu açıksa uygulamayı imzalayıp kurar. Sertifika yoksa Samsung girişi istenir.</summary>
    private async Task RefreshSamsungAppAsync(LgTv target)
    {
        if (services.BundledApp(TvBrand.Samsung) is not { } bundled) return;
        if (!NeedsInstall(bundled))
        {
            TvApp = new TvAppState(TvAppKind.UpToDate, InstalledAppVersion ?? bundled.Version);
            return;
        }
        if (!await services.IsSamsungDevModeOnAsync(target.Host))
        {
            TvApp = new TvAppState(TvAppKind.DevModeOff);
            return;
        }
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            await services.InstallSamsungAppAsync(target.Host, bundled, e => post(() => InstallEvent(e)));
            store.Save(StoreKeys.TvAppVersion, bundled.Version);
            TvApp = new TvAppState(TvAppKind.UpToDate, bundled.Version);
            await OpenTvAppAfterInstallAsync(target);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    /// <summary>Android TV: ağda ADB'si açık TV'yi bulur, uygulamayı kurar ve açar.</summary>
    private async Task RefreshAndroidAppAsync(LgTv target)
    {
        if (services.BundledApp(TvBrand.AndroidTv) is not { } bundled) return;
        var host = target.Host;
        if (host.Length == 0 || !await services.IsAndroidDebugOnAsync(host))
        {
            // Uygulama kuruluysa ve TV'de hata ayıklama kapalıysa yapılacak bir şey yok.
            if (!NeedsInstall(bundled))
            {
                TvApp = new TvAppState(TvAppKind.UpToDate, InstalledAppVersion ?? bundled.Version);
                return;
            }
            if ((await services.ScanAndroidTvsAsync()).FirstOrDefault() is not { } found)
            {
                TvApp = new TvAppState(TvAppKind.DevModeOff);
                return;
            }
            host = found;
        }
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var version = await services.InstallAndroidAppAsync(host, bundled, e => post(() => InstallEvent(e)));
            // TV'nin adresi bundan sonra sürüm denetimi için kullanılır.
            var located = target with { Host = host };
            tv = located;
            store.Save(StoreKeys.Tv, located);
            store.Save(StoreKeys.TvAppVersion, version);
            Changed(nameof(Tv));
            TvApp = new TvAppState(TvAppKind.UpToDate, version);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    private void InstallEvent(TvInstallEvent e)
    {
        // Kurulum bittikten sonra gelen geç olay sonucu ezmesin.
        if (TvApp.Kind is not (TvAppKind.Working or TvAppKind.SignIn or TvAppKind.ApproveOnTv)) return;
        switch (e.Kind)
        {
            case TvInstallEventKind.SignIn when e.Url is { } url:
                TvApp = new TvAppState(TvAppKind.SignIn, Url: url);
                OpenInBrowser(url);
                break;
            case TvInstallEventKind.ApproveOnTv:
                TvApp = new TvAppState(TvAppKind.ApproveOnTv);
                break;
            case TvInstallEventKind.Progress when TvApp.Kind == TvAppKind.SignIn:
                TvApp = new TvAppState(TvAppKind.Working);
                break;
        }
    }

    /// <summary>Key Server'dan anahtarı alır (TV ekranındaki parolayla) ve uygulamayı kurar.</summary>
    public async Task InstallTvAppAsync(string passphrase)
    {
        if (tv is not { } target || services.BundledTvAppVersion is not { } bundled) return;
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var devKey = await services.FetchDevKeyAsync(target.Host, passphrase.Trim());
            store.Save(StoreKeys.TvDevKey, devKey);
            await InstallTvAppAsync(target, devKey, bundled);
        }
        catch (DevModeException error)
        {
            TvApp = new TvAppState(TvAppKind.NeedsPassphrase, Error: error.Error);
        }
    }

    private async Task InstallTvAppAsync(LgTv target, string devKey, string bundled)
    {
        TvApp = new TvAppState(TvAppKind.Working);
        try
        {
            var installed = await services.InstalledTvAppVersionAsync(target.Host, devKey);
            if (installed is not null && !DevModeInstaller.IsNewer(bundled, installed))
            {
                TvApp = new TvAppState(TvAppKind.UpToDate, installed);
                await RenewDevSessionIfDueAsync(target, devKey);
                return;
            }
            await services.InstallTvAppAsync(target.Host, devKey);
            TvApp = new TvAppState(TvAppKind.UpToDate, bundled);
            tvAppMissing = false;
            await RenewDevSessionIfDueAsync(target, devKey);
            await OpenTvAppAfterInstallAsync(target);
        }
        catch (DevModeException error) when (error.Error == DevModeError.KeyRefused)
        {
            // Geliştirici modu yeniden açılınca TV yeni anahtar üretir; eskisi işe yaramaz.
            store.Remove(StoreKeys.TvDevKey);
            TvApp = new TvAppState(TvAppKind.NeedsPassphrase);
        }
        catch (DevModeException error)
        {
            TvApp = FailedState(error);
        }
    }

    /// <summary>
    /// LG geliştirici modunun 1000 saatlik süresini günde en çok bir kez baştan başlatır; kullanıcı TV'den uzatmak
    /// zorunda kalmaz.
    /// </summary>
    private async Task RenewDevSessionIfDueAsync(LgTv target, string devKey)
    {
        var last = store.Load<DateTimeOffset?>(StoreKeys.DevSessionRenewed) ?? DateTimeOffset.MinValue;
        if (DateTimeOffset.UtcNow - last < TimeSpan.FromHours(20)) return;
        if (await services.RenewDevSessionAsync(target.Host, devKey) is { } hours)
        {
            DevModeHoursLeft = hours;
            store.Save(StoreKeys.DevSessionRenewed, (DateTimeOffset?)DateTimeOffset.UtcNow);
        }
    }

    private async Task OpenTvAppAfterInstallAsync(LgTv target)
    {
        if (keeper is not null)
        {
            keeper.Reconnect();
            return;
        }
        try { await services.PairTvAsync(target, store.Load<string>(StoreKeys.TvKey), ServerAddress(target)); }
        catch (Exception) { /* TV uygulaması kuruldu; açılmadıysa kullanıcı açabilir */ }
    }

    /// <summary>TV ağda yeni bir adreste bulundu.</summary>
    public void TvMoved(LgTv moved)
    {
        tv = moved;
        store.Save(StoreKeys.Tv, moved);
        Changed(nameof(Tv));
    }

    public void StartModelDownload()
    {
        if (ModelInstalled || modelDownload is { IsCompleted: false }) return;
        ModelDownloading = true;
        ModelError = null;
        var download = services.Translation.DownloadAsync(settings.SourceLanguage, settings.TargetLanguage, new ImmediateProgress(value => post(() => ModelProgress = value)), CancellationToken.None);
        modelDownload = FinishDownload(download);
    }

    private async Task FinishDownload(Task download)
    {
        try
        {
            await download;
            post(() => ModelProgress = 1);
        }
        catch (Exception error)
        {
            post(() => ModelError = error.Message);
        }
        finally
        {
            post(() =>
            {
                ModelDownloading = false;
                Changed(nameof(ModelInstalled), nameof(CanAdvance));
                RefreshTranslatorStatus();
                // Çeviri sürerken model indiyse hat yeni çevirmenle kurulur.
                if (isRunning && TranslatorStatus == TranslatorStatus.Ready) MakePipeline();
            });
        }
    }

    public Task WaitForModelAsync() => modelDownload ?? Task.CompletedTask;

    public void SendTestSubtitle()
    {
        server.SendStyle(settings.Style);
        // Oyunda henüz altyazı görülmediyse altyazı bölgesinin altına yakın tipik bir yer kullanılır.
        var region = settings.Region;
        var box = lastSubtitleBox ?? new TextBox(0.3, region.Bottom - 0.1, 0.7, region.Bottom - 0.03);
        SendSubtitle(text["This is a test subtitle. Can you see it on top of the game?"], 8000, box);
    }

    /// <summary>
    /// Num 0: canlı altyazı çevirisini duraklatır ya da sürdürür. Alan çevirirken sürekli çeviri de TV'de görünüp
    /// karışmasın diye kullanılır. TV'de kısa bir bildirim çıkar. Çeviri sürmüyorsa bir şey yapmaz.
    /// </summary>
    public void ToggleSubtitles()
    {
        if (!isRunning) return;
        var pause = !subtitlesPaused;
        SubtitlesPaused = pause;
        if (pause)
        {
            pipeline?.Reset();
            ClearSubtitle();
        }
        server.SendStyle(settings.Style);
        SendSubtitle(text[pause ? "Subtitle translation paused" : "Subtitle translation resumed"], 1800);
    }

    /// <summary>Num 8: ayar ekranı, oyunun şu anki görüntüsünü yeniden alsın.</summary>
    public event Action? SnapshotRequested;

    public void RequestSnapshot() => SnapshotRequested?.Invoke();

    /// <summary>Çeviri bilgisayarda da gösterilecekse (metin, kalma süresi ms) ile çağrılır.</summary>
    public event Action<string, int>? PcResultReady;

    /// <summary>
    /// 1, 2 ya da 3 numaralı ek bölgeyi şimdi bir kez okuyup çevirir; sonuç ayardaki yere (TV, bilgisayar ya da ikisi) gider
    /// (görev paneli gibi yazılar için). Çeviri sürmüyorsa, bölge kapalıysa ya da önceki istek bitmediyse hiçbir şey yapılmaz.
    /// </summary>
    public async Task<ExtraRegionOutcome> TranslateExtraRegionAsync(int slot)
    {
        if (settings.ExtraRegion(slot) is not { } region) return ExtraRegionOutcome.NotConfigured;
        if (lastFrame is not { } frame) return ExtraRegionOutcome.NotRunning;
        var target = settings.ExtraTarget(slot);
        return (await RunRegionAsync(frame, region, target != ResultTarget.Pc, target != ResultTarget.Tv)).Outcome;
    }

    /// <summary>
    /// Serbest alan (Num 5): verilen karedeki alanı bir kez okuyup çevirir. Sonuç, ayardaki hedefe göre TV'ye de gönderilir;
    /// bilgisayarda gösterilmek üzere her durumda döner (serbest alan penceresi kendisi gösterir).
    /// </summary>
    public Task<(ExtraRegionOutcome Outcome, RegionTranslation.Result? Result)> TranslateAreaAsync(Frame frame, CropRegion region) =>
        RunRegionAsync(frame, region, settings.FreeAreaTarget != ResultTarget.Pc, false);

    private async Task<(ExtraRegionOutcome Outcome, RegionTranslation.Result? Result)> RunRegionAsync(Frame frame, CropRegion region, bool toTv, bool toPcPopup)
    {
        var reader = activeReader;
        if (!isRunning || reader is null) return (ExtraRegionOutcome.NotRunning, null);
        // Tuşa üst üste basılınca ikinci bir okuma başlamaz.
        if (Interlocked.Exchange(ref extraBusy, 1) == 1) return (ExtraRegionOutcome.Busy, null);
        try
        {
            RegionTranslation.Result? result;
            try
            {
                result = await RegionTranslation.TranslateOnceAsync(frame, region, reader, MakeTranslator());
            }
            catch (TranslatorException error)
            {
                post(() => TranslatorStatus = error.Status);
                return (ExtraRegionOutcome.TranslatorUnavailable, null);
            }
            catch
            {
                return (ExtraRegionOutcome.Failed, null);
            }
            if (result is null)
            {
                var message = text["No text found in this area."];
                if (toTv)
                {
                    server.SendStyle(settings.Style);
                    server.SendSubtitle(message, 2500, new TextBox(region.Left, region.Top, region.Right, region.Bottom));
                }
                if (toPcPopup) post(() => PcResultReady?.Invoke(message, 2500));
                return (ExtraRegionOutcome.NoText, null);
            }
            var milliseconds = RegionTranslation.DisplayMilliseconds(result.Translation);
            if (toTv)
            {
                server.SendStyle(settings.Style);
                server.SendSubtitle(result.Translation, milliseconds, result.Box, result.Structured);
            }
            post(() =>
            {
                LastSource = result.Source;
                LastTranslation = result.Translation;
                if (toPcPopup) PcResultReady?.Invoke(result.Translation, milliseconds);
            });
            return (ExtraRegionOutcome.Shown, result);
        }
        finally
        {
            Volatile.Write(ref extraBusy, 0);
        }
    }

    /// <summary>Süren "ekranı çevir" işi; yeniden basılınca iptal edilir.</summary>
    private CancellationTokenSource? screenWork;
    /// <summary>TV'deki ekran çevirilerinin kalkacağı an; bu andan önce yeniden basmak onları kaldırır.</summary>
    private DateTime screenShownUntil;

    /// <summary>
    /// Num 9: oyunun şu anki karesinin tamamını bir kez okur, her yazı grubunu ayrı çevirip TV'de kendi yerine koyar.
    /// Çeviriler hazırlandıkça TV'ye gider. Çeviri sürerken ya da çeviriler TV'deyken yeniden basmak onları kaldırır.
    /// </summary>
    public async Task<ScreenOutcome> TranslateScreenAsync()
    {
        var reader = activeReader;
        if (!isRunning || reader is null || lastFrame is not { } frame) return ScreenOutcome.NotRunning;
        if (screenWork is { } running)
        {
            running.Cancel();
            screenWork = null;
            HideScreen();
            return ScreenOutcome.Cleared;
        }
        if (DateTime.UtcNow < screenShownUntil)
        {
            HideScreen();
            return ScreenOutcome.Cleared;
        }

        using var work = new CancellationTokenSource();
        screenWork = work;
        server.SendStyle(settings.Style);
        server.SendSubtitle(text["Translating the screen…"], 2500);
        try
        {
            var translator = MakeTranslator();
            var items = await ScreenTranslation.TranslateAsync(frame, reader, translator, shown =>
            {
                if (work.IsCancellationRequested) return;
                server.SendScreen(shown, ScreenTranslation.DisplayMilliseconds(shown));
                screenShownUntil = DateTime.UtcNow.AddMilliseconds(ScreenTranslation.DisplayMilliseconds(shown));
            }, work.Token);
            server.Clear();
            if (items.Count == 0)
            {
                server.SendSubtitle(text["No text found on the screen."], 2500);
                return ScreenOutcome.NoText;
            }
            return ScreenOutcome.Shown;
        }
        catch (OperationCanceledException)
        {
            return ScreenOutcome.Cleared;
        }
        catch (TranslatorException error)
        {
            server.Clear();
            post(() => TranslatorStatus = error.Status);
            return ScreenOutcome.TranslatorUnavailable;
        }
        catch
        {
            server.Clear();
            return ScreenOutcome.Failed;
        }
        finally
        {
            if (screenWork == work) screenWork = null;
        }
    }

    private void HideScreen()
    {
        screenShownUntil = default;
        server.ClearScreen();
        server.Clear();
    }

    /// <summary>Kurulumu baştan başlatır (kayıtlı her şeyi siler).</summary>
    public void ResetSetup()
    {
        Stop();
        foreach (var key in new[] { StoreKeys.AccountId, StoreKeys.Console, StoreKeys.Tv, StoreKeys.TvKey, StoreKeys.TvDevKey, StoreKeys.TvAppVersion }) store.Remove(key);
        accountId = null;
        console = null;
        tv = null;
        tvAppMissing = false;
        TvApp = TvAppState.Unknown;
        Step = OnboardingStep.Welcome;
        captureTarget = null;
        Changed(nameof(HasAccount), nameof(Console), nameof(Tv), nameof(CaptureTitle), nameof(ShowsTvStatus));
        // Kurulum baştan başlar; kaynak hoş geldin ekranında yeniden seçilir.
        if (IsWindowSource) Settings = settings with { Source = VideoSource.Ps5 };
    }

    // --- Çeviri ---

    public async Task StartAsync()
    {
        // Düğmeye iki kez basılması ya da beklerken tekrar çağrılması ikinci bir oturum açmaz.
        if (isRunning || isStarting) return;
        var window = IsWindowSource;
        // PS5 kaynağında hesap, PS5 ve TV gerekir; pencere kaynağında hiçbiri gerekmez (TV varsa kullanılır).
        if (!window && (console is null || accountId is null || tv is null)) return;
        IsStarting = true;
        try
        {
            // Pencere seçilmediyse ya da kapandıysa önce Windows'un pencere seçicisi açılır; vazgeçilirse başlamaz.
            if (window && captureTarget is not { IsClosed: false })
            {
                if (await services.PickWindowAsync() is not { } picked) return;
                CaptureTarget = picked;
            }

            RefreshTranslatorStatus();
            try { await StartServerAsync(); } catch { /* port açılamadıysa TV durumu "bekleniyor" kalır */ }
            server.SendStyle(settings.Style);
            SubtitlesPaused = false;
            MakePipeline();
            if (settings.Dubbing.Enabled && DubbingInstalled) MakeDubber()?.Prepare();

            Attach(window ? captureTarget!.MakeStream() : services.MakeStream(console!, accountId!));

            // TV uygulamasını başlat ve açık tut; TV kapalıysa açıldığında bağlanır.
            if (tv is { } target)
            {
                var running = server;
                var tvKeeper = services.MakeKeeper(target, store.Load<string>(StoreKeys.TvKey),
                    new Dictionary<string, string> { ["server"] = ServerAddress(target) },
                    () => running.ConnectedClients > 0, moved => post(() => TvMoved(moved)));
                tvKeeper.Start(TimeSpan.FromSeconds(5));
                keeper = tvKeeper;
                _ = RefreshTvAppAsync();
            }

            sleep.Begin();
            IsRunning = true;
        }
        finally
        {
            IsStarting = false;
        }
    }

    /// <summary>Oturumun olaylarına bağlanır ve başlatır. Yerine yenisi geçmiş oturumun geç gelen durumu yok sayılır.</summary>
    private void Attach(IStreamSession stream)
    {
        stream.StateChanged += state => post(() =>
        {
            if (ReferenceEquals(session, stream)) SessionStateChanged(state);
        });
        stream.FrameReceived += HandleFrame;
        session = stream;
        stream.Start();
    }

    private void DetachSession()
    {
        if (session is not { } stopping) return;
        session = null;
        stopping.FrameReceived -= HandleFrame;
        stopping.Stop();
        stopping.Dispose();
    }

    private ICaptureTarget? CaptureTarget
    {
        get => captureTarget;
        set
        {
            captureTarget = value;
            Changed(nameof(CaptureTitle), nameof(Ps5StatusText));
        }
    }

    /// <summary>
    /// "Pencere seç": Windows'un pencere seçicisini açar. Çeviri sürüyorsa okuma hemen yeni pencereye geçer;
    /// sürmüyorsa pencere bir sonraki başlatmada kullanılır. Vazgeçilirse önceki pencere kalır.
    /// </summary>
    public async Task ChooseWindowAsync()
    {
        if (choosingWindow) return;
        choosingWindow = true;
        try
        {
            if (await services.PickWindowAsync() is not { } picked) return;
            CaptureTarget = picked;
            if (!isRunning || !IsWindowSource) return;
            DetachSession();
            pipeline?.Reset();
            ClearSubtitle();
            Attach(picked.MakeStream());
        }
        finally
        {
            choosingWindow = false;
        }
    }

    public void Stop()
    {
        DetachSession();
        // Bilgisayardaki katmanda altyazı kalmasın (TV uygulaması kendi süresiyle kaldırır).
        if (isRunning && ShowsSubtitlesOnPc) post(() => PcSubtitleChanged?.Invoke(null));
        keeper?.Stop();
        keeper = null;
        pipeline?.Reset();
        pipeline = null;
        activeReader = null;
        screenWork?.Cancel();
        screenWork = null;
        if (DateTime.UtcNow < screenShownUntil) server.ClearScreen();
        screenShownUntil = default;
        SubtitlesPaused = false;
        ActiveSourceLanguage = null;
        StopDubber();
        if (isRunning) sleep.End();
        IsRunning = false;
        SessionState = SessionState.Disconnected(DisconnectKind.Stopped);
    }

    /// <summary>Bilgisayar uykudan uyanınca bağlantıları hemen yenile.</summary>
    public void SystemResumed()
    {
        session?.ReconnectNow();
        keeper?.Reconnect();
    }

    /// <summary>Çeviri başlamamışken bağlı olmamak bir sorun değildir; nokta gri kalır.</summary>
    public static StatusTone Ps5ToneFor(SessionState state, bool running) =>
        state.Phase == SessionPhase.Streaming ? StatusTone.Good : running ? StatusTone.Warning : StatusTone.Idle;

    public static StatusTone TvToneFor(int clients, bool running) =>
        clients > 0 ? StatusTone.Good : running ? StatusTone.Warning : StatusTone.Idle;

    /// <summary>PS5 durumunun kullanıcıya gösterilecek metni.</summary>
    public static string StatusText(SessionState state, bool running, Localizer text)
    {
        switch (state.Phase)
        {
            case SessionPhase.Streaming: return text["Connected"];
            case SessionPhase.Connecting: return text["Connecting…"];
        }
        if (!running) return text["Not connected"];
        return text[state.Reason?.Kind switch
        {
            DisconnectKind.ConsoleAsleep => "Turn on your PS5",
            DisconnectKind.InUse => "Remote Play is in use on another device",
            DisconnectKind.Refused => "The PS5 refused the connection. Set up the PS5 again.",
            DisconnectKind.LoginPasscode => "The second user has a login passcode on the PS5. Turn it off in Settings › Users and Accounts.",
            DisconnectKind.WindowClosed => "The window was closed. Choose a window again.",
            _ => "Reconnecting…",
        }];
    }

    /// <summary>Pencere kaynağının durum metni: okunurken pencerenin başlığı.</summary>
    private string WindowStatusText()
    {
        if (sessionState.Phase == SessionPhase.Disconnected && sessionState.Reason?.Kind == DisconnectKind.WindowClosed && isRunning)
            return text["The window was closed. Choose a window again."];
        if (sessionState.Phase == SessionPhase.Connecting) return text["Connecting…"];
        return captureTarget?.Title is { Length: > 0 } title ? title : text["No window chosen"];
    }

    public void Dispose()
    {
        Stop();
        server.Dispose();
    }

    // --- İç işleyiş ---

    /// <summary>Okuma hattını güncel dillerle (yeniden) kurar.</summary>
    private void MakePipeline()
    {
        pipeline?.Reset();
        pipeline = null;
        ActiveSourceLanguage = settings.SourceLanguage;
        var reader = services.MakeReader(settings.SourceLanguage);
        activeReader = reader;
        OcrMissing = reader is null || !services.HasOcrLanguage(settings.SourceLanguage);
        if (reader is null) return;
        // Hattın gönderdiği her altyazı ve temizleme TV'ye ve bilgisayardaki katmana aynı anda gider.
        var built = new SubtitlePipeline(reader, MakeTranslator(), new MirroredSender(this), new ReadScheduler());
        built.PairShown += (source, translation) =>
        {
            var box = built.LastBox;
            post(() =>
            {
                LastSource = source;
                LastTranslation = translation;
                if (box is not null) lastSubtitleBox = box;
            });
        };
        // Model bu arada inmiş olabilir; her çeviri sonucu durumu günceller.
        built.TranslationResult += error => post(() => TranslatorStatus = error ?? TranslatorStatus.Ready);
        built.SpeechReady += lines => post(() => Speak(lines));
        pipeline = built;
    }

    private ITextTranslator MakeTranslator()
    {
        var local = MakeLocalTranslator();
        if (OnlineTranslationActive && store.Load<string>(StoreKeys.DeepLKey) is { } key &&
            services.MakeOnlineTranslator(key, local is UnavailableTranslator ? null : local) is { } online)
            return online;
        return local;
    }

    private ITextTranslator MakeLocalTranslator()
    {
        var translation = services.Translation;
        var (source, target) = (settings.SourceLanguage, settings.TargetLanguage);
        if (!translation.IsSupported(source, target)) return new UnavailableTranslator(TranslatorStatus.Unsupported);
        return translation.IsInstalled(source, target) ? translation.Create(source, target) : new UnavailableTranslator(TranslatorStatus.NeedsDownload);
    }

    /// <summary>DeepL yalnız İngilizce→Türkçe için açılır (ayar ekranındaki dil seçenekleri de bunlar).</summary>
    private bool OnlineTranslationActive =>
        settings.OnlineTranslation && settings.SourceLanguage == "en" && settings.TargetLanguage == "tr" && HasDeepLKey;

    /// <summary>Kayıtlı bir DeepL anahtarı var mı (anahtarın kendisi arayüze hiç dönmez).</summary>
    public bool HasDeepLKey => store.Load<string>(StoreKeys.DeepLKey) is { Length: > 0 };

    /// <summary>Anahtar denenir; geçerliyse (kotası bitmiş olsa da) şifreli depoya yazılır ve çevrimiçi çeviri açılır.</summary>
    public async Task<OnlineKeyCheck> SetDeepLKeyAsync(string key)
    {
        key = key.Trim();
        var check = await services.CheckOnlineKeyAsync(key);
        if (check.State is OnlineKeyState.Valid or OnlineKeyState.QuotaExceeded)
        {
            store.Save(StoreKeys.DeepLKey, key);
            Changed(nameof(HasDeepLKey));
            if (settings.OnlineTranslation && isRunning) MakePipeline();
            else Settings = settings with { OnlineTranslation = true };
            RefreshTranslatorStatus();
        }
        return check;
    }

    /// <summary>Kayıtlı anahtarın bu ayki kullanımı.</summary>
    public Task<OnlineKeyCheck> CheckDeepLKeyAsync() =>
        store.Load<string>(StoreKeys.DeepLKey) is { Length: > 0 } key ? services.CheckOnlineKeyAsync(key)
            : Task.FromResult(new OnlineKeyCheck(OnlineKeyState.Invalid));

    public void RemoveDeepLKey()
    {
        store.Remove(StoreKeys.DeepLKey);
        Changed(nameof(HasDeepLKey));
        Settings = settings with { OnlineTranslation = false };
        RefreshTranslatorStatus();
    }

    private void RefreshTranslatorStatus()
    {
        var translation = services.Translation;
        TranslatorStatus = OnlineTranslationActive ? TranslatorStatus.Ready
            : !translation.IsSupported(settings.SourceLanguage, settings.TargetLanguage) ? TranslatorStatus.Unsupported
            : translation.IsInstalled(settings.SourceLanguage, settings.TargetLanguage) ? TranslatorStatus.Ready
            : TranslatorStatus.NeedsDownload;
    }

    private void SessionStateChanged(SessionState state)
    {
        SessionState = state;
        // Bağlantı koptuysa TV'de eski altyazı kalmasın.
        if (state.Phase == SessionPhase.Disconnected)
        {
            pipeline?.Reset();
            ClearSubtitle();
            dubber?.Stop();
        }
    }

    /// <summary>
    /// Altyazı her seçili ekrana çıkar: TV uygulaması (ve OBS katmanı) sunucudan alır; pencere kaynağında bilgisayardaki
    /// katman da <see cref="PcSubtitleChanged"/> ile alır.
    /// </summary>
    private void SendSubtitle(string message, int durationMs, TextBox? box = null)
    {
        server.SendSubtitle(message, durationMs, box);
        if (ShowsSubtitlesOnPc) post(() => PcSubtitleChanged?.Invoke(new PcSubtitle(message, durationMs, box)));
    }

    private void ClearSubtitle()
    {
        server.Clear();
        if (ShowsSubtitlesOnPc) post(() => PcSubtitleChanged?.Invoke(null));
    }

    /// <summary>
    /// Bilgisayardaki altyazı katmanı: altyazı gelince gösterilir (kalma süresi ms), null gelince kaldırılır. Arayüz iş
    /// parçacığında çağrılır; yalnız <see cref="ShowsSubtitlesOnPc"/> iken altyazı gelir.
    /// </summary>
    public event Action<PcSubtitle?>? PcSubtitleChanged;

    /// <summary>Okuma hattının göndericisi; sunucu değişse de (port doluysa) güncel olanı kullanır.</summary>
    private sealed class MirroredSender(AppController owner) : ISubtitleSender
    {
        public void SendSubtitle(string text, int durationMs = 15000, TextBox? box = null) => owner.SendSubtitle(text, durationMs, box);
        public void Clear() => owner.ClearSubtitle();
    }

    private async void HandleFrame(Frame frame)
    {
        post(() => LastFrame = frame);
        // Duraklatıldıysa kare yalnız ek bölgeler ve serbest alan için saklanır; altyazı okunmaz.
        if (subtitlesPaused) return;
        // Önceki kare hâlâ işleniyorsa bu kare atlanır; okuma kareleri biriktirmez.
        var current = pipeline;
        if (current is null || Interlocked.Exchange(ref processing, 1) == 1) return;
        try
        {
            await current.ProcessAsync(frame, settings.Region);
        }
        catch
        {
            // Tek bir karenin okunamaması akışı durdurmaz.
        }
        finally
        {
            Volatile.Write(ref processing, 0);
        }
    }

    private async Task StartServerAsync()
    {
        if (serverStarted) return;
        try
        {
            ServerPort = await server.StartAsync();
        }
        catch (SocketException)
        {
            // 8787 başka bir uygulamada; TV'ye adres her seferinde bildirildiği için herhangi bir boş port olur.
            server.ClientsChanged -= OnClientsChanged;
            server = new SubtitleServer(0);
            server.ClientsChanged += OnClientsChanged;
            ServerPort = await server.StartAsync();
        }
        serverStarted = true;
    }

    private void OnClientsChanged(int count) => post(() => TvClients = count);

    /// <summary>TV ile aynı ağdaki bilgisayar adresi.</summary>
    private string ServerAddress(LgTv target) => $"http://{LocalAddress(target.Host) ?? "127.0.0.1"}:{ServerPort}";

    public static string? LocalAddress(string near)
    {
        if (!IPAddress.TryParse(near, out var target) || target.AddressFamily != AddressFamily.InterNetwork) return null;
        string? fallback = null;
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (network.OperationalStatus != OperationalStatus.Up || network.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var unicast in network.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                var mask = unicast.IPv4Mask.GetAddressBytes();
                var address = unicast.Address.GetAddressBytes();
                var wanted = target.GetAddressBytes();
                if (Enumerable.Range(0, 4).All(i => (address[i] & mask[i]) == (wanted[i] & mask[i]))) return unicast.Address.ToString();
                fallback ??= unicast.Address.ToString();
            }
        }
        return fallback;
    }

    // --- Uygulama güncellemesi ---

    /// <summary>GitHub'da daha yeni sürüm varsa numarası.</summary>
    public string? UpdateVersion { get => updateVersion; private set => Set(ref updateVersion, value); }
    public bool Updating { get => updating; private set => Set(ref updating, value); }
    public double UpdateProgress { get => updateProgress; private set => Set(ref updateProgress, value); }
    public string? UpdateError { get => updateError; private set => Set(ref updateError, value); }
    /// <summary>Kurulum dosyasıyla kurulduysa tek tıkla güncellenir; değilse sürüm sayfası açılır.</summary>
    public bool CanUpdateItself => services.Updater?.CanUpdateItself == true;

    /// <summary>Açılışta bir kez; ağ yoksa sessizce geçer.</summary>
    public async Task CheckForUpdateAsync()
    {
        if (services.Updater is not { } updater) return;
        try { UpdateVersion = await updater.CheckAsync(); }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException) { }
    }

    public async Task InstallUpdateAsync()
    {
        if (services.Updater is not { } updater || updating) return;
        if (!updater.CanUpdateItself)
        {
            OpenInBrowser(updater.DownloadPage);
            return;
        }
        Updating = true;
        UpdateError = null;
        try
        {
            // Çeviri sürüyorsa önce durur; uygulama güncellenip yeniden açılır.
            Stop();
            await updater.InstallAsync(new ImmediateProgress(value => post(() => UpdateProgress = value)));
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                          or UnauthorizedAccessException)
        {
            UpdateError = error.Message;
        }
        finally
        {
            Updating = false;
        }
    }

    // --- Türkçe dublaj ---

    /// <summary>Bu derlemede dublaj var mı (ses motoru uygulamayla geliyor mu).</summary>
    public bool DubbingAvailable => services.Dubbing is not null || services.FastDubbing is not null;
    public bool FastDubbingAvailable => services.FastDubbing is not null;
    /// <summary>Ayarlarda seçili motor; hızlı ses yoksa doğal sese düşer.</summary>
    private IDubbingModel? ActiveDubbing =>
        settings.Dubbing.Engine == DubbingEngine.Fast && services.FastDubbing is { } fast ? fast : services.Dubbing;
    public bool DubbingInstalled => ActiveDubbing?.IsInstalled == true;
    public long DubbingDownloadBytes => services.Dubbing?.DownloadBytes ?? 0;
    public bool DubbingDownloading { get => dubbingDownloading; private set => Set(ref dubbingDownloading, value); }
    public double DubbingProgress { get => dubbingProgress; private set => Set(ref dubbingProgress, value); }
    public string? DubbingError { get => dubbingError; private set => Set(ref dubbingError, value); }

    /// <summary>Ses modelini indirir (ilk kullanımda bir kez, ~440 MB).</summary>
    public async Task DownloadDubbingAsync()
    {
        if (services.Dubbing is not { } model || dubbingDownloading || model.IsInstalled) return;
        DubbingDownloading = true;
        DubbingError = null;
        try
        {
            await Task.Run(() => model.DownloadAsync(new ImmediateProgress(value => post(() => DubbingProgress = value)), CancellationToken.None));
            if (settings.Dubbing.Enabled && isRunning) MakeDubber()?.Prepare();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or TaskCanceledException
                                          or UnauthorizedAccessException)
        {
            DubbingError = error.Message;
        }
        finally
        {
            DubbingDownloading = false;
            Changed(nameof(DubbingInstalled));
        }
    }

    /// <summary>Ayarlardaki "Dinle": sesi bilgisayardan çalar.</summary>
    public async Task PreviewVoiceAsync(DubbingVoice voice)
    {
        if (!DubbingInstalled) return;
        DubbingError = null;
        try { await StartServerAsync(); } catch (SocketException) { return; }
        MakeDubber()?.Preview(voice);
    }

    public void AssignVoice(DubbingVoice voice, string name)
    {
        var cast = settings.Dubbing.Cast.Copy();
        cast.Assign(voice, name);
        Settings = settings with { Dubbing = settings.Dubbing with { Cast = cast } };
    }

    private Dubber? MakeDubber()
    {
        if (dubber is not null) return dubber;
        if (ActiveDubbing is not { } model || services.Audio is not { } audio) return null;
        var made = new Dubber(model, audio, () => server, () => ServerPort, post)
        {
            Cast = settings.Dubbing.Cast.Copy(),
            Output = settings.Dubbing.Output,
        };
        made.CastChanged += cast => Settings = settings with { Dubbing = settings.Dubbing with { Cast = cast } };
        made.Failed += message => DubbingError = message;
        dubber = made;
        return made;
    }

    private void Speak(IReadOnlyList<SpokenLine> lines)
    {
        if (!settings.Dubbing.Enabled || !DubbingInstalled) return;
        MakeDubber()?.Speak(lines);
    }

    private void DubbingSettingsChanged(DubbingSettings old)
    {
        var now = settings.Dubbing;
        if (now.Engine != old.Engine)
        {
            // Motor değişti: eski dublaj kapanır, yenisi hazırlanır.
            StopDubber();
            Changed(nameof(DubbingInstalled));
            if (now.Enabled && isRunning && DubbingInstalled) MakeDubber()?.Prepare();
            return;
        }
        if (dubber is null)
        {
            if (now.Enabled && !old.Enabled && isRunning && DubbingInstalled) MakeDubber()?.Prepare();
            return;
        }
        // Dublajın kendi atadığı ses (CastChanged) zaten dublajda; kullanıcının seçimi kopyalanır.
        if (!ReferenceEquals(now.Cast, old.Cast)) dubber.Cast = now.Cast.Copy();
        if (now.Output != old.Output)
        {
            dubber.Stop();
            dubber.Output = now.Output;
        }
        if (!now.Enabled) StopDubber();
    }

    private void StopDubber()
    {
        if (dubber is not { } stopping) return;
        dubber = null;
        _ = stopping.DisposeAsync().AsTask();
    }

    private AppException Error(AppError error) => new(error, AppException.Describe(error, text));

    /// <summary>Değer değiştiyse yazar ve özelliği (ve ona bağlı olanları) bildirir.</summary>
    private bool Set<T>(ref T field, T value, string[]? also = null, [CallerMemberName] string name = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed([name, .. also ?? []]);
        return true;
    }

    private void Changed(params string[] names)
    {
        foreach (var name in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Model eksikken ya da dil desteklenmezken kullanılan çevirmen: nedeni bildirir.</summary>
    private sealed class UnavailableTranslator(TranslatorStatus status) : ITextTranslator
    {
        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken = default) => throw new TranslatorException(status);
    }

    /// <summary>İlerlemeyi bağlama yakalamadan doğrudan iletir (post zaten arayüze aktarır).</summary>
    private sealed class ImmediateProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}

/// <summary>Ek bölgenin tek seferlik çevirisinin sonucu.</summary>
public enum ExtraRegionOutcome { Shown, NoText, NotConfigured, NotRunning, Busy, TranslatorUnavailable, Failed }

/// <summary>"Ekranı çevir"in sonucu; <see cref="Cleared"/>: TV'deki çeviriler kaldırıldı ya da süren iş iptal edildi.</summary>
public enum ScreenOutcome { Shown, NoText, Cleared, NotRunning, TranslatorUnavailable, Failed }
