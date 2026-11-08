// SPDX-License-Identifier: AGPL-3.0-only

using CouchSubs.Core;

namespace CouchSubs.RemotePlay;

// Remote Play katmanının uygulamaya açtığı türler (Mac'teki RemotePlay modülünün karşılıkları).

public sealed record ConsoleInfo(string Host, string Name, bool IsAwake);

/// <summary>PS5'e kaydedilmiş bu bilgisayarın bağlantı bilgileri. Şifreli olarak saklanır.</summary>
/// <param name="RegistKey">16 baytlık kayıt anahtarı (ASCII onaltılık, sıfırla doldurulmuş).</param>
/// <param name="Morning">16 baytlık oturum anahtarı.</param>
public sealed record RegisteredConsole(string Host, string Name, byte[] RegistKey, byte[] Morning)
{
    /// <summary>Uyandırma için kayıt anahtarının onaltılık değeri.</summary>
    public ulong? WakeCredential
    {
        get
        {
            var end = Array.IndexOf(RegistKey, (byte)0);
            var text = System.Text.Encoding.ASCII.GetString(RegistKey, 0, end < 0 ? RegistKey.Length : end);
            return ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber, null, out var value) ? value : null;
        }
    }

    public RegisteredConsole WithHost(string host) => this with { Host = host };
}

/// <summary>Bağlantının neden koptuğu; kullanıcıya anlatılabilecek biçimde.</summary>
public enum DisconnectKind
{
    Stopped,
    /// <summary>PS5 kapalı ya da uyandırılamadı.</summary>
    ConsoleAsleep,
    /// <summary>Remote Play başka bir cihazda kullanılıyor.</summary>
    InUse,
    /// <summary>PS5 bağlantıyı reddetti (ör. kayıt geçersiz ya da Uzaktan Oynatma kapalı).</summary>
    Refused,
    Network,
    /// <summary>İkinci kullanıcının PS5'te giriş şifresi var; Uzaktan Oynatma şifre istiyor.</summary>
    LoginPasscode,
    Other,
    /// <summary>Görüntüsü okunan pencere kapandı (pencere kaynağı).</summary>
    WindowClosed,
}

public sealed record DisconnectReason(DisconnectKind Kind, string? Detail = null);

public enum SessionPhase { Connecting, Streaming, Disconnected }

public sealed record SessionState(SessionPhase Phase, DisconnectReason? Reason = null)
{
    public static readonly SessionState Connecting = new(SessionPhase.Connecting);
    public static readonly SessionState Streaming = new(SessionPhase.Streaming);
    public static SessionState Disconnected(DisconnectKind kind, string? detail = null) => new(SessionPhase.Disconnected, new DisconnectReason(kind, detail));
}

public enum RegistrationFailure
{
    Failed,
    Canceled,
    /// <summary>PS5 hesabı tanımadı: kod, uygulamada giriş yapılan hesabın PS5 kullanıcısından alınmamış.</summary>
    WrongAccount,
}

public sealed class RegistrationException(RegistrationFailure failure) : Exception($"PS5 registration failed: {failure}")
{
    public RegistrationFailure Failure { get; } = failure;
}

/// <summary>PS5'e Remote Play bağlantısı (gerçekte StreamSession). Ses kapalıdır, kol girdisi gönderilmez.</summary>
public interface IStreamSession : IDisposable
{
    /// <summary>Çözülmüş kare (en çok saniyede 15). Oturumun kendi iş parçacığında çağrılır.</summary>
    event Action<Frame>? FrameReceived;
    event Action<SessionState>? StateChanged;
    void Start();
    /// <summary>Bloklamaz: oturum arka planda kapatılır.</summary>
    void Stop();
    /// <summary>Bilgisayar uykudan uyandığında ya da ağ değiştiğinde çağrılır.</summary>
    void ReconnectNow();
}
