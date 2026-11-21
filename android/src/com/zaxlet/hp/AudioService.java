package com.zaxlet.hp;

import android.app.*;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.SystemClock;
import android.util.Log;
import java.util.ArrayDeque;
import java.util.Arrays;

/**
 * Audio yang diputar HP (Reels, video, dll) ditangkap lewat AudioPlaybackCapture lalu dikirim ke PC (/aud/ws) sebagai PCM 16-bit stereo 48 kHz,
 * potongan 20 ms. Selama aktif volume musik HP dibuat 0 supaya suaranya hanya keluar di PC; volume lama dikembalikan saat berhenti.
 * Android 10+ dan izin rekam layar (dialog "Mulai sekarang") wajib tiap kali dimulai.
 */
public class AudioService extends Service {
    private static final String TAG = "ZaxLetAud";
    private static final int FRAME_BYTES = 3840;   // 20 ms @ 48 kHz stereo 16-bit (<= 6144, kelipatan 4)
    private static final int MAX_QUEUE_FRAMES = 20;   // ~400 ms batas antrean sebelum buang potongan terlama

    static volatile boolean alive;
    static volatile String err = "";
    private volatile boolean stopping;
    private MediaProjection mp;
    private AudioRecord rec;
    private Ws ws;
    private AudioManager am;
    private WifiManager.WifiLock wifiLock;
    private Thread audThread;
    private Thread sendThread;
    private final Object qLock = new Object();
    private final ArrayDeque<byte[]> queue = new ArrayDeque<>(MAX_QUEUE_FRAMES);
    private int droppedChunks;
    private int sentFrames;
    private long maxSendMs;

    @Override public IBinder onBind(Intent i) { return null; }

    @Override public int onStartCommand(Intent in, int flags, int startId) {
        if (in != null && "STOP".equals(in.getAction())) { shutdown(); return START_NOT_STICKY; }
        Intent data = in == null ? null : (Intent) in.getParcelableExtra("data");
        if (alive || data == null) return START_NOT_STICKY;
        Net.init(this);
        err = "";
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        nm.createNotificationChannel(new NotificationChannel("zaxlet-aud", "Audio ke PC", NotificationManager.IMPORTANCE_LOW));
        PendingIntent stop = PendingIntent.getService(this, 2, new Intent(this, AudioService.class).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE);
        Notification n = new Notification.Builder(this, "zaxlet-aud").setSmallIcon(android.R.drawable.ic_media_play).setContentTitle("Audio HP → PC")
                .setContentText("Speaker HP senyap, suara diputar di PC").setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "Matikan", stop).build()).build();
        try {
            startForeground(2, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);   // wajib sebelum getMediaProjection (Android 14)
            mp = ((MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE)).getMediaProjection(in.getIntExtra("rc", 0), data);
            mp.registerCallback(new MediaProjection.Callback() { @Override public void onStop() { shutdown(); } }, null);
            AudioPlaybackCaptureConfiguration cfg = new AudioPlaybackCaptureConfiguration.Builder(mp)
                    .addMatchingUsage(AudioAttributes.USAGE_MEDIA).addMatchingUsage(AudioAttributes.USAGE_GAME).addMatchingUsage(AudioAttributes.USAGE_UNKNOWN).build();
            AudioFormat fmt = new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_16BIT).setSampleRate(48000).setChannelMask(AudioFormat.CHANNEL_IN_STEREO).build();
            int minBuf = AudioRecord.getMinBufferSize(48000, AudioFormat.CHANNEL_IN_STEREO, AudioFormat.ENCODING_PCM_16BIT);
            int recBuf = Math.max(FRAME_BYTES * 12, minBuf > 0 ? minBuf * 2 : 0);   // >= 240 ms agar tidak overrun saat CPU sibuk
            rec = new AudioRecord.Builder().setAudioPlaybackCaptureConfig(cfg).setAudioFormat(fmt).setBufferSizeInBytes(recBuf).build();
        } catch (Throwable e) { err = "Audio gagal: " + e.getMessage(); alive = true; shutdown(); return START_NOT_STICKY; }
        am = (AudioManager) getSystemService(AUDIO_SERVICE);
        alive = true; stopping = false;
        audThread = new Thread(this::run, "zaxlet-aud");
        audThread.start();
        return START_NOT_STICKY;
    }

    private void run() {
        byte[] buf = new byte[FRAME_BYTES];   // 20 ms
        int shortOrErrReads = 0;
        long lastLogMs = SystemClock.elapsedRealtime();
        try {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
            Ws w = new Ws("/aud/ws");   // sambung dulu: bila PC tidak terjangkau, speaker HP tidak sempat dibisukan
            synchronized (this) {
                if (stopping) { w.close(); return; }
                ws = w;
                WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                if (wm != null) {
                    int mode = Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                    wifiLock = wm.createWifiLock(mode, "zaxlet:aud");
                    wifiLock.setReferenceCounted(false);
                    wifiLock.acquire();
                }
            }
            w.sock.setTcpNoDelay(true);
            w.sock.setSendBufferSize(65536);
            synchronized (qLock) {
                queue.clear();
                droppedChunks = 0;
                sentFrames = 0;
                maxSendMs = 0;
            }
            synchronized (this) {
                if (stopping) return;
                sendThread = new Thread(() -> sendLoop(w), "zaxlet-aud-tx");
                sendThread.start();
                rec.startRecording();
                mute(true);
            }
            int tick = 0;
            while (!stopping) {
                int n = rec.read(buf, 0, buf.length);
                if (n < 0) { shortOrErrReads++; break; }
                if (n < buf.length) shortOrErrReads++;
                int valid = n & ~3;
                if (valid > 0) {
                    byte[] chunk = Arrays.copyOf(buf, valid);
                    synchronized (qLock) {
                        while (queue.size() >= MAX_QUEUE_FRAMES) {
                            queue.pollFirst();
                            droppedChunks++;
                        }
                        queue.addLast(chunk);
                        qLock.notifyAll();
                    }
                }
                if (++tick % 25 == 0) {
                    synchronized (this) {
                        if (!stopping && am.getStreamVolume(AudioManager.STREAM_MUSIC) != 0) am.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0);   // tombol volume tidak boleh membunyikan speaker
                    }
                }
                long now = SystemClock.elapsedRealtime();
                if (now - lastLogMs >= 5000) {
                    int qSz, drops, sent;
                    long maxMs;
                    synchronized (qLock) {
                        qSz = queue.size();
                        drops = droppedChunks;
                        sent = sentFrames;
                        maxMs = maxSendMs;
                        maxSendMs = 0;
                    }
                    Log.i(TAG, "sent=" + sent + " q=" + qSz + " drop=" + drops + " shortErr=" + shortOrErrReads + " maxSendMs=" + maxMs);
                    lastLogMs = now;
                }
            }
        } catch (Throwable e) { if (!stopping) err = "Audio berhenti: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
        finally {
            stopping = true;
            synchronized (qLock) { qLock.notifyAll(); }
            synchronized (this) {
                try { if (ws != null) { ws.close(); ws = null; } } catch (Exception ignored) { }
            }
            if (sendThread != null) {
                sendThread.interrupt();
                try { sendThread.join(1000); } catch (InterruptedException ignored) { }
                sendThread = null;
            }
            cleanup();
        }
    }

    private void sendLoop(Ws w) {
        try {
            while (!stopping) {
                byte[] chunk;
                synchronized (qLock) {
                    while (queue.isEmpty() && !stopping) {
                        try { qLock.wait(); } catch (InterruptedException e) { return; }
                    }
                    if (stopping) break;
                    chunk = queue.pollFirst();
                }
                if (chunk == null) continue;
                long t0 = SystemClock.elapsedRealtime();
                w.sendBinary(chunk, chunk.length);
                long dt = SystemClock.elapsedRealtime() - t0;
                synchronized (qLock) {
                    sentFrames++;
                    if (dt > maxSendMs) maxSendMs = dt;
                }
            }
        } catch (Throwable e) {
            if (!stopping) {
                err = "Audio berhenti: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage());
                shutdown();
            }
        }
    }

    /** Volume lama disimpan di prefs: bila proses mati saat senyap, sesi berikutnya tetap memulihkan volume asli (bukan 0). */
    private void mute(boolean on) {
        try {
            int old = Net.sp.getInt("audvol", -1);
            if (on) {
                if (old < 0) Net.sp.edit().putInt("audvol", am.getStreamVolume(AudioManager.STREAM_MUSIC)).apply();
                am.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0);
            } else if (old >= 0) {
                am.setStreamVolume(AudioManager.STREAM_MUSIC, old, 0);
                Net.sp.edit().remove("audvol").apply();
            }
        } catch (Exception e) { }
    }

    private synchronized void shutdown() {
        if (!alive) return;
        stopping = true;
        synchronized (qLock) { qLock.notifyAll(); }
        try { if (ws != null) ws.close(); } catch (Exception e) { }
        try { if (rec != null) rec.stop(); } catch (Exception e) { }
        if (audThread == null) cleanup();
    }

    // lepas rec dan pulihkan volume hanya setelah zaxlet-aud keluar agar tidak berpacu dengan rec.read/setStreamVolume
    private synchronized void cleanup() {
        alive = false; stopping = true; audThread = null;
        synchronized (qLock) { queue.clear(); qLock.notifyAll(); }
        try { if (ws != null) { ws.close(); ws = null; } } catch (Exception e) { }
        try { if (rec != null) { rec.stop(); rec.release(); rec = null; } } catch (Exception e) { }
        try { if (mp != null) { mp.stop(); mp = null; } } catch (Exception e) { }
        try { if (wifiLock != null) { if (wifiLock.isHeld()) wifiLock.release(); wifiLock = null; } } catch (Exception e) { }
        if (am != null) mute(false);
        stopForeground(true);
        stopSelf();
    }

    @Override public void onDestroy() { shutdown(); super.onDestroy(); }
}
