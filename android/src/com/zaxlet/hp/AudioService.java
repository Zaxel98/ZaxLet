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
import java.io.IOException;
import java.nio.ByteBuffer;
import java.util.ArrayDeque;
import java.util.Arrays;

/**
 * Audio yang diputar HP (Reels, video, dll) ditangkap lewat AudioPlaybackCapture lalu dikirim ke PC (/aud/opus atau fallback /aud/ws),
 * potongan 20 ms. Selama aktif volume musik HP dibuat 0 supaya suaranya hanya keluar di PC; volume lama dikembalikan saat berhenti.
 * Android 10+ dan izin rekam layar (dialog "Mulai sekarang") wajib tiap kali dimulai.
 */
public class AudioService extends Service {
    private static final String TAG = "ZaxLetAud";
    private static final int FRAME_BYTES = 3840;   // 20 ms @ 48 kHz stereo 16-bit (<= 6144, kelipatan 4)
    private static final int MAX_QUEUE_FRAMES = 20;   // ~400 ms batas antrean sebelum buang potongan terlama

    static volatile boolean alive;
    static volatile boolean videoActive;
    static volatile int audioQueued;
    static volatile String err = "";
    private volatile boolean stopping;
    private volatile boolean usePcmFallback;
    private MediaProjection mp;
    private ScreenStream screenStream;
    private AudioRecord rec;
    private Ws ws;
    private AudioManager am;
    private WifiManager.WifiLock wifiLock;
    private Thread audThread;
    private Thread sendThread;
    private final Object qLock = new Object();
    private final Object wake = new Object();
    private final ArrayDeque<byte[]> queue = new ArrayDeque<>(MAX_QUEUE_FRAMES);
    private int droppedChunks;
    private int sentFrames;
    private long sentBytes;
    private long maxSendMs;

    @Override public IBinder onBind(Intent i) { return null; }

    private Notification note(String text) {
        PendingIntent stop = PendingIntent.getService(this, 2, new Intent(this, AudioService.class).setAction("STOP"), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, "zaxlet-aud").setSmallIcon(android.R.drawable.ic_media_play).setContentTitle("Audio HP → PC")
                .setContentText(text).setOngoing(true)
                .addAction(new Notification.Action.Builder(null, "Matikan", stop).build()).build();
    }

    private void setNote(String text) {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            if (nm != null) nm.notify(2, note(text));
        } catch (Exception ignored) { }
    }

    @Override public int onStartCommand(Intent in, int flags, int startId) {
        if (in != null && "STOP".equals(in.getAction())) { shutdown(); return START_NOT_STICKY; }
        Intent data = in == null ? null : (Intent) in.getParcelableExtra("data");
        if (alive || data == null) return START_NOT_STICKY;
        Net.init(this);
        err = "";
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        nm.createNotificationChannel(new NotificationChannel("zaxlet-aud", "Audio ke PC", NotificationManager.IMPORTANCE_LOW));
        try {
            startForeground(2, note("Menyambung ulang…"), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION);   // wajib sebelum getMediaProjection (Android 14)
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
        audioQueued = 0; usePcmFallback = false;
        videoActive = in.getBooleanExtra("video", false);
        if (videoActive) {
            screenStream = new ScreenStream(this, mp, () -> {
                screenStream = null;
                videoActive = false;
                if (!stopping) setNote("Speaker HP senyap, suara diputar di PC");
            });
            screenStream.start();
        }
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
            synchronized (this) {
                if (stopping) return;
                WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
                if (wm != null) {
                    int mode = Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                    wifiLock = wm.createWifiLock(mode, "zaxlet:aud");
                    wifiLock.setReferenceCounted(false);
                    wifiLock.acquire();
                }
                sendThread = new Thread(this::sendLoop, "zaxlet-aud-tx");
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
                if (valid > 0 && ws != null) {
                    byte[] chunk = Arrays.copyOf(buf, valid);
                    synchronized (qLock) {
                        if (ws != null) {
                            while (queue.size() >= MAX_QUEUE_FRAMES) {
                                queue.pollFirst();
                                droppedChunks++;
                            }
                            queue.addLast(chunk);
                            audioQueued = queue.size();
                            qLock.notifyAll();
                        }
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
                    long maxMs, bytes;
                    synchronized (qLock) {
                        qSz = queue.size();
                        drops = droppedChunks;
                        sent = sentFrames;
                        bytes = sentBytes;
                        maxMs = maxSendMs;
                        maxSendMs = 0;
                    }
                    String codec = usePcmFallback ? "pcm" : "opus";
                    long avgPkt = sent > 0 ? bytes / sent : 0;
                    Log.i(TAG, "codec=" + codec + " avgPkt=" + avgPkt + " sent=" + sent + " q=" + qSz + " drop=" + drops + " shortErr=" + shortOrErrReads + " maxSendMs=" + maxMs);
                    lastLogMs = now;
                }
            }
        } catch (Throwable e) { if (!stopping) err = "Audio berhenti: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
        finally {
            stopping = true;
            synchronized (qLock) {
                ws = null;
                queue.clear();
                audioQueued = 0;
                qLock.notifyAll();
            }
            synchronized (wake) { wake.notifyAll(); }
            if (sendThread != null) {
                sendThread.interrupt();
                try { sendThread.join(1000); } catch (InterruptedException ignored) { }
                sendThread = null;
            }
            cleanup();
        }
    }

    private void sendLoop() {
        int backoff = 1000;
        boolean everConnected = false;
        while (!stopping) {
            Ws w = null;
            MediaCodec enc = null;
            boolean immediateReconnect = false;
            try {
                if (!usePcmFallback) {
                    try {
                        MediaFormat fmt = MediaFormat.createAudioFormat(MediaFormat.MIMETYPE_AUDIO_OPUS, 48000, 2);
                        fmt.setInteger(MediaFormat.KEY_BIT_RATE, 64000);
                        enc = MediaCodec.createEncoderByType(MediaFormat.MIMETYPE_AUDIO_OPUS);
                        enc.configure(fmt, null, null, MediaCodec.CONFIGURE_FLAG_ENCODE);
                        enc.start();
                    } catch (Throwable t) {
                        Log.w(TAG, "Opus encoder gagal dibuat, fallback ke PCM: " + t.getMessage());
                        if (enc != null) {
                            try { enc.stop(); } catch (Exception ignored) { }
                            try { enc.release(); } catch (Exception ignored) { }
                            enc = null;
                        }
                        usePcmFallback = true;
                    }
                }

                if (!usePcmFallback) {
                    try {
                        w = new Ws("/aud/opus");
                    } catch (IOException e) {
                        String msg = e.getMessage();
                        if (msg != null && msg.startsWith("ditolak:")) {
                            Log.w(TAG, "Server tolak /aud/opus (" + msg + "), fallback ke PCM");
                            if (enc != null) {
                                try { enc.stop(); } catch (Exception ignored) { }
                                try { enc.release(); } catch (Exception ignored) { }
                                enc = null;
                            }
                            usePcmFallback = true;
                            w = new Ws("/aud/ws");
                        } else {
                            throw e;
                        }
                    }
                } else {
                    w = new Ws("/aud/ws");
                }

                if (stopping) { w.close(); break; }
                w.sock.setTcpNoDelay(true);
                w.sock.setSendBufferSize(65536);
                backoff = 1000;
                everConnected = true;
                synchronized (qLock) {
                    queue.clear();
                    audioQueued = 0;
                    droppedChunks = 0;
                    sentFrames = 0;
                    sentBytes = 0;
                    maxSendMs = 0;
                    ws = w;
                }
                setNote(videoActive ? "Layar + audio HP -> PC" : "Speaker HP senyap, suara diputar di PC");

                long totalSamples = 0;
                long firstFeedMs = 0;
                boolean gotFirstPacket = false;
                MediaCodec.BufferInfo bufInfo = enc != null ? new MediaCodec.BufferInfo() : null;

                while (!stopping) {
                    byte[] chunk;
                    synchronized (qLock) {
                        while (queue.isEmpty() && !stopping) {
                            try { qLock.wait(); } catch (InterruptedException e) { return; }
                        }
                        if (stopping) break;
                        chunk = queue.pollFirst();
                        audioQueued = queue.size();
                    }
                    if (chunk == null) continue;

                    if (enc == null) {
                        long t0 = SystemClock.elapsedRealtime();
                        w.sendBinary(chunk, chunk.length);
                        long dt = SystemClock.elapsedRealtime() - t0;
                        synchronized (qLock) {
                            sentFrames++;
                            sentBytes += chunk.length;
                            if (dt > maxSendMs) maxSendMs = dt;
                        }
                    } else {
                        int inIdx = enc.dequeueInputBuffer(20000);
                        if (inIdx < 0) {
                            if (drainOpus(enc, bufInfo, w)) gotFirstPacket = true;
                            inIdx = enc.dequeueInputBuffer(20000);
                        }
                        if (inIdx >= 0) {
                            ByteBuffer inBuf = enc.getInputBuffer(inIdx);
                            if (inBuf != null) {
                                inBuf.clear();
                                int toWrite = Math.min(chunk.length, inBuf.remaining());
                                inBuf.put(chunk, 0, toWrite);
                                long ptsUs = (totalSamples * 1_000_000L) / 48000L;
                                totalSamples += toWrite / 4;
                                enc.queueInputBuffer(inIdx, 0, toWrite, ptsUs, 0);
                                if (firstFeedMs == 0) firstFeedMs = SystemClock.elapsedRealtime();
                            }
                        }
                        if (drainOpus(enc, bufInfo, w)) gotFirstPacket = true;

                        if (!gotFirstPacket && firstFeedMs > 0 && SystemClock.elapsedRealtime() - firstFeedMs >= 1000) {
                            Log.w(TAG, "Opus tidak mengeluarkan paket dalam 1 dtk, fallback ke PCM");
                            usePcmFallback = true;
                            immediateReconnect = true;
                            break;
                        }
                    }
                }
            } catch (Throwable ignored) {
                // sambungan putus/gagal: pertahankan tangkapan audio & senyap, buang antrean basi, coba sambung lagi
                if (!everConnected && !stopping) { err = "Audio gagal: PC tidak terjangkau"; shutdown(); return; }   // sambungan pertama gagal: berhenti seperti dulu, HP tidak dibisukan tanpa tujuan
            } finally {
                synchronized (qLock) {
                    if (ws == w) ws = null;
                    queue.clear();
                    audioQueued = 0;
                }
                if (enc != null) {
                    try { enc.stop(); } catch (Exception ignored) { }
                    try { enc.release(); } catch (Exception ignored) { }
                    enc = null;
                }
                if (w != null) try { w.close(); } catch (Exception ignored) { }
            }
            if (stopping) break;
            if (immediateReconnect) continue;
            setNote("Menyambung ulang…");
            synchronized (wake) {
                if (!stopping) {
                    try { wake.wait(backoff); } catch (InterruptedException e) { break; }
                }
            }
            backoff = Math.min(backoff * 2, 10000);
        }
    }

    private boolean drainOpus(MediaCodec enc, MediaCodec.BufferInfo bufInfo, Ws w) throws IOException {
        boolean produced = false;
        long timeoutUs = 20000;
        while (!stopping) {
            int outIdx = enc.dequeueOutputBuffer(bufInfo, timeoutUs);
            if (outIdx == MediaCodec.INFO_TRY_AGAIN_LATER) {
                break;
            } else if (outIdx == MediaCodec.INFO_OUTPUT_FORMAT_CHANGED) {
                continue;
            } else if (outIdx >= 0) {
                timeoutUs = 0;
                boolean isConfig = (bufInfo.flags & MediaCodec.BUFFER_FLAG_CODEC_CONFIG) != 0;
                byte[] pkt = null;
                if (!isConfig && bufInfo.size > 0) {
                    ByteBuffer outBuf = enc.getOutputBuffer(outIdx);
                    if (outBuf != null) {
                        outBuf.position(bufInfo.offset);
                        outBuf.limit(bufInfo.offset + bufInfo.size);
                        pkt = new byte[bufInfo.size];
                        outBuf.get(pkt);
                    }
                }
                enc.releaseOutputBuffer(outIdx, false);
                if (pkt != null) {
                    produced = true;
                    long t0 = SystemClock.elapsedRealtime();
                    w.sendBinary(pkt, pkt.length);
                    long dt = SystemClock.elapsedRealtime() - t0;
                    synchronized (qLock) {
                        sentFrames++;
                        sentBytes += pkt.length;
                        if (dt > maxSendMs) maxSendMs = dt;
                    }
                }
            }
        }
        return produced;
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
        synchronized (wake) { wake.notifyAll(); }
        ScreenStream ss = screenStream;
        if (ss != null) try { ss.stop(); } catch (Exception e) { }
        Ws w = ws;
        if (w != null) try { w.close(); } catch (Exception e) { }
        try { if (rec != null) rec.stop(); } catch (Exception e) { }
        if (audThread == null) cleanup();
    }

    // lepas rec dan pulihkan volume hanya setelah zaxlet-aud keluar agar tidak berpacu dengan rec.read/setStreamVolume
    private synchronized void cleanup() {
        alive = false; videoActive = false; stopping = true; audThread = null;
        ScreenStream ss = screenStream; screenStream = null;
        if (ss != null) try { ss.stop(); } catch (Exception e) { }
        synchronized (qLock) { queue.clear(); audioQueued = 0; qLock.notifyAll(); }
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
