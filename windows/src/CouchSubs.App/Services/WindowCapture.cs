// SPDX-License-Identifier: AGPL-3.0-only

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using CouchSubs.Core;
using CouchSubs.RemotePlay;
using CouchSubs.Shell;
using Windows.Foundation.Metadata;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;

namespace CouchSubs.App.Services;

/// <summary>
/// Pencere kaynağı: Windows'un kendi pencere seçicisiyle (Windows.Graphics.Capture) seçilen pencerenin görüntüsü.
/// Xbox uygulaması, tarayıcıda xbox.com/play, yakalama kartı önizlemesi ya da bir PC oyunu olabilir.
/// </summary>
public static partial class WindowCapture
{
    /// <summary>Bu Windows pencere görüntüsü alabiliyor mu (Windows 10 1903 ve sonrası).</summary>
    public static bool IsSupported
    {
        get
        {
            try { return GraphicsCaptureSession.IsSupported(); }
            catch (Exception) { return false; }
        }
    }

    /// <summary>Seçiciyi <paramref name="owner"/> penceresinin üstünde açar; vazgeçilirse null.</summary>
    public static async Task<ICaptureTarget?> PickAsync(IntPtr owner)
    {
        if (!IsSupported) return null;
        var picker = new GraphicsCapturePicker();
        // Masaüstü uygulamasında seçici hangi pencereye ait olduğunu bilmeli.
        WinRT.Interop.InitializeWithWindow.Initialize(picker, owner);
        var item = await picker.PickSingleItemAsync();
        return item is null ? null : new Target(item, Direct3D.CreateDevice());
    }

    /// <summary>Seçilen pencere; aynı öğeden her başlatmada yeni bir oturum açılır.</summary>
    private sealed class Target : ICaptureTarget
    {
        private readonly GraphicsCaptureItem item;
        private readonly IDirect3DDevice device;
        private volatile bool closed;

        public Target(GraphicsCaptureItem item, IDirect3DDevice device)
        {
            this.item = item;
            this.device = device;
            item.Closed += (_, _) => closed = true;
        }

        public string Title => item.DisplayName;
        public bool IsClosed => closed;
        public IStreamSession MakeStream() => new WindowCaptureSession(item, device, () => closed);
    }

    /// <summary>Windows.Graphics.Capture'ın istediği Direct3D aygıtı (d3d11.dll ile).</summary>
    private static partial class Direct3D
    {
        private const int DriverTypeHardware = 1;
        private const int DriverTypeWarp = 5;
        private const uint CreateDeviceBgraSupport = 0x20;
        private const uint SdkVersion = 7;
        private static readonly Guid DxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

        [LibraryImport("d3d11.dll")]
        private static partial int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

        [LibraryImport("d3d11.dll")]
        private static partial int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        public static IDirect3DDevice CreateDevice()
        {
            // Ekran kartı sürücüsü yoksa (sanal makine) yazılımla çizen WARP kullanılır.
            var result = D3D11CreateDevice(IntPtr.Zero, DriverTypeHardware, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion,
                out var device, out _, out var context);
            if (result < 0)
                result = D3D11CreateDevice(IntPtr.Zero, DriverTypeWarp, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion,
                    out device, out _, out context);
            Marshal.ThrowExceptionForHR(result);
            try
            {
                var iid = DxgiDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
                    finally { Marshal.Release(inspectable); }
                }
                finally { Marshal.Release(dxgi); }
            }
            finally
            {
                if (context != IntPtr.Zero) Marshal.Release(context);
                Marshal.Release(device);
            }
        }
    }
}

/// <summary>
/// Pencerenin karelerini BGRA <see cref="Frame"/> olarak verir. Kareler saniyede en çok 10 kez alınır (okuma bundan
/// hızlı değil; fazlası işlemciyi boşuna yorar). Pencere kapanınca <see cref="SessionPhase.Disconnected"/> bildirilir.
/// </summary>
public sealed class WindowCaptureSession(GraphicsCaptureItem item, IDirect3DDevice device, Func<bool> isClosed) : IStreamSession
{
    private static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(100);

    private readonly object gate = new();
    private Direct3D11CaptureFramePool? pool;
    private GraphicsCaptureSession? capture;
    private SizeInt32 size;
    private DateTime lastFrame = DateTime.MinValue;
    private int converting;
    private bool stopped;

    public event Action<Frame>? FrameReceived;
    public event Action<SessionState>? StateChanged;

    public void Start()
    {
        if (isClosed())
        {
            StateChanged?.Invoke(SessionState.Disconnected(DisconnectKind.WindowClosed));
            return;
        }
        StateChanged?.Invoke(SessionState.Connecting);
        try
        {
            lock (gate)
            {
                stopped = false;
                size = item.Size;
                // Kareler kendi iş parçacığında gelir (arayüz iş parçacığını bekletmez).
                pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                pool.FrameArrived += OnFrameArrived;
                capture = pool.CreateCaptureSession(item);
                // Fare imleci altyazının üstüne gelip okumayı bozmasın.
                if (ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                    capture.IsCursorCaptureEnabled = false;
                capture.StartCapture();
            }
            item.Closed += OnClosed;
            StateChanged?.Invoke(SessionState.Streaming);
        }
        catch (Exception error)
        {
            Close();
            StateChanged?.Invoke(SessionState.Disconnected(DisconnectKind.Other, error.Message));
        }
    }

    public void Stop()
    {
        Close();
        StateChanged?.Invoke(SessionState.Disconnected(DisconnectKind.Stopped));
    }

    /// <summary>Pencere görüntüsü uykudan sonra da sürer; yeniden bağlanacak bir şey yoktur.</summary>
    public void ReconnectNow() { }

    public void Dispose() => Close();

    private void OnClosed(GraphicsCaptureItem sender, object args)
    {
        Close();
        StateChanged?.Invoke(SessionState.Disconnected(DisconnectKind.WindowClosed));
    }

    private void Close()
    {
        item.Closed -= OnClosed;
        lock (gate)
        {
            stopped = true;
            if (pool is not null) pool.FrameArrived -= OnFrameArrived;
            capture?.Dispose();
            capture = null;
            pool?.Dispose();
            pool = null;
        }
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null) return;
        lock (gate)
        {
            if (stopped) return;
            // Pencere boyutu değiştiyse havuz yeni boyutla yeniden kurulur; bu kare atlanır.
            if (frame.ContentSize.Width != size.Width || frame.ContentSize.Height != size.Height)
            {
                size = frame.ContentSize;
                if (size.Width > 0 && size.Height > 0) sender.Recreate(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
                return;
            }
        }
        var now = DateTime.UtcNow;
        if (now - lastFrame < MinInterval || FrameReceived is null) return;
        // Önceki kare hâlâ kopyalanıyorsa bu kare atlanır; kareler birikmez.
        if (Interlocked.Exchange(ref converting, 1) == 1) return;
        lastFrame = now;
        try
        {
            if (Copy(frame) is { } image) FrameReceived?.Invoke(image);
        }
        catch (Exception)
        {
            // Tek karenin alınamaması (ör. pencere küçültüldü) akışı durdurmaz.
        }
        finally
        {
            Volatile.Write(ref converting, 0);
        }
    }

    /// <summary>Ekran kartındaki kareyi belleğe BGRA olarak kopyalar.</summary>
    private static Frame? Copy(Direct3D11CaptureFrame frame)
    {
        using var bitmap = SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult();
        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        if (width <= 0 || height <= 0) return null;
        using var bgra = bitmap.BitmapPixelFormat == BitmapPixelFormat.Bgra8 ? null : SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var source = bgra ?? bitmap;
        var pixels = new byte[width * height * 4];
        source.CopyToBuffer(pixels.AsBuffer());
        return new Frame(pixels, width, height, width * 4);
    }
}
