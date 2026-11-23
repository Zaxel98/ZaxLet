using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ZaxLet;

/// <summary>
/// Active phone screen session from /scr/ws: decodes latest JPEG frames on a background thread,
/// stores resolution/error state, and sends control commands back to the phone.
/// </summary>
public static class PhoneScreen
{
    static readonly object ConnLock = new();
    static readonly object DecodeLock = new();

    static Stream _stream;
    static SemaphoreSlim _writeLock;
    static ConcurrentQueue<byte[]> _sendQueue;
    static int _sending;
    static long _connId;

    static byte[] _pendingJpeg;
    static bool _decoding;
    static int _uiPending;

    static long _fpsWindowStart;
    static int _fpsCount;

    public static bool Connected { get; private set; }
    public static int Width { get; private set; }
    public static int Height { get; private set; }
    public static int Rotation { get; private set; }
    public static string Error { get; private set; } = "";
    public static double Fps { get; private set; }
    public static BitmapSource LatestFrame { get; private set; }
    public static long ConnId => Volatile.Read(ref _connId);

    /// <summary>Register a new /scr/ws socket. Old connection (if any) is closed.</summary>
    public static SemaphoreSlim Attach(Stream ns)
    {
        Stream oldStream;
        SemaphoreSlim writeLock;
        lock (ConnLock)
        {
            oldStream = _stream;
            _stream = ns;
            writeLock = new SemaphoreSlim(1, 1);
            _writeLock = writeLock;
            _sendQueue = new ConcurrentQueue<byte[]>();
            _sending = 0;
            Interlocked.Increment(ref _connId);
            Connected = true;
            Error = "";
            Width = 0;
            Height = 0;
            Rotation = 0;
            Fps = 0;
            _fpsCount = 0;
            _fpsWindowStart = Environment.TickCount64;
            LatestFrame = null;
        }
        if (oldStream != null)
        {
            try { oldStream.Dispose(); } catch { }
        }
        return writeLock;
    }

    /// <summary>Detach socket when read loop ends; ignore if already replaced by a new connection.</summary>
    public static void Detach(Stream ns)
    {
        bool closedActive = false;
        lock (ConnLock)
        {
            if (_stream == ns)
            {
                _stream = null;
                _writeLock = null;
                _sendQueue = null;
                Connected = false;
                closedActive = true;
            }
        }
        if (closedActive)
            Application.Current?.Dispatcher.BeginInvoke(ScreenWindow.OnDisconnected);
    }

    /// <summary>Close active connection from the PC side (e.g., when window is closed manually).</summary>
    public static void Disconnect()
    {
        Stream s;
        lock (ConnLock)
        {
            s = _stream;
            _stream = null;
            _writeLock = null;
            _sendQueue = null;
            Connected = false;
        }
        if (s != null)
        {
            try { s.Dispose(); } catch { }
        }
    }

    /// <summary>Send a WebSocket text frame (server->client, unmasked, FIN+text) to the phone. Never throws.</summary>
    public static void Send(string json)
    {
        if (string.IsNullOrEmpty(json)) return;
        Stream s;
        SemaphoreSlim wl;
        ConcurrentQueue<byte[]> q;
        lock (ConnLock)
        {
            s = _stream;
            wl = _writeLock;
            q = _sendQueue;
        }
        if (s == null || wl == null || q == null) return;

        try
        {
            var payload = Encoding.UTF8.GetBytes(json);
            byte[] frame;
            if (payload.Length < 126)
            {
                frame = new byte[2 + payload.Length];
                frame[0] = 0x81;
                frame[1] = (byte)payload.Length;
                Buffer.BlockCopy(payload, 0, frame, 2, payload.Length);
            }
            else if (payload.Length <= 65535)
            {
                frame = new byte[4 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 126;
                frame[2] = (byte)(payload.Length >> 8);
                frame[3] = (byte)(payload.Length & 255);
                Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
            }
            else
            {
                frame = new byte[10 + payload.Length];
                frame[0] = 0x81;
                frame[1] = 127;
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(frame.AsSpan(2, 8), (ulong)payload.Length);
                Buffer.BlockCopy(payload, 0, frame, 10, payload.Length);
            }
            q.Enqueue(frame);
            if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
                _ = Task.Run(() => DrainSendQueue(s, wl, q));
        }
        catch { }
    }

    static async Task DrainSendQueue(Stream s, SemaphoreSlim wl, ConcurrentQueue<byte[]> q)
    {
        try
        {
            while (q.TryDequeue(out var frame))
            {
                await wl.WaitAsync();
                try
                {
                    await s.WriteAsync(frame);
                    await s.FlushAsync();
                }
                finally { wl.Release(); }
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _sending, 0);
            if (!q.IsEmpty && Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
                _ = Task.Run(() => DrainSendQueue(s, wl, q));
        }
    }

    public static void OnText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("t", out var tProp)) return;
            var t = tProp.GetString();
            if (t == "info")
            {
                if (root.TryGetProperty("w", out var w) && w.TryGetInt32(out var wi)) Width = wi;
                if (root.TryGetProperty("h", out var h) && h.TryGetInt32(out var hi)) Height = hi;
                if (root.TryGetProperty("rot", out var r) && r.TryGetInt32(out var ri)) Rotation = ri;
                Error = "";
                NotifyUi();
            }
            else if (t == "err")
            {
                if (root.TryGetProperty("m", out var m)) Error = m.GetString() ?? "";
                NotifyUi();
            }
        }
        catch { }
    }

    public static void PushFrame(byte[] jpeg)
    {
        if (jpeg == null || jpeg.Length < 4) return;

        var now = Environment.TickCount64;
        _fpsCount++;
        var dt = now - _fpsWindowStart;
        if (dt >= 1000)
        {
            Fps = _fpsCount * 1000.0 / dt;
            _fpsCount = 0;
            _fpsWindowStart = now;
        }

        lock (DecodeLock)
        {
            _pendingJpeg = jpeg;
            if (_decoding) return;
            _decoding = true;
        }
        _ = Task.Run(DecodeLoop);
    }

    static void DecodeLoop()
    {
        while (true)
        {
            byte[] jpeg;
            lock (DecodeLock)
            {
                jpeg = _pendingJpeg;
                _pendingJpeg = null;
                if (jpeg == null)
                {
                    _decoding = false;
                    return;
                }
            }
            try
            {
                using var ms = new MemoryStream(jpeg);
                var frame = BitmapDecoder.Create(ms, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                frame.Freeze();
                LatestFrame = frame;
                NotifyUi();
            }
            catch { }
        }
    }

    static void NotifyUi()
    {
        if (Interlocked.Exchange(ref _uiPending, 1) != 0) return;
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _uiPending, 0);
            ScreenWindow.OnUpdate();
        });
    }
}
