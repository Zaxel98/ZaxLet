package com.zaxlet.hp;

import android.app.*;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.media.*;
import android.media.projection.MediaProjection;
import android.media.projection.MediaProjectionManager;
import android.os.IBinder;

/**
 * Audio yang diputar HP (Reels, video, dll) ditangkap lewat AudioPlaybackCapture lalu dikirim ke PC (/aud/ws) sebagai PCM 16-bit stereo 48 kHz,
 * potongan 10 ms. Selama aktif volume musik HP dibuat 0 supaya suaranya hanya keluar di PC; volume lama dikembalikan saat berhenti.
 * Android 10+ dan izin rekam layar (dialog "Mulai sekarang") wajib tiap kali dimulai.
 */
public class AudioService extends Service {
    static volatile boolean alive;
    static volatile String err = "";
    private volatile boolean stopping;
    private MediaProjection mp;
    private AudioRecord rec;
    private Ws ws;
    private AudioManager am;

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
            rec = new AudioRecord.Builder().setAudioPlaybackCaptureConfig(cfg).setAudioFormat(fmt).setBufferSizeInBytes(1920 * 8).build();   // ~80 ms: buffer lebih besar = tundaan lebih besar saat pengirim tersendat
        } catch (Throwable e) { err = "Audio gagal: " + e.getMessage(); alive = true; shutdown(); return START_NOT_STICKY; }
        am = (AudioManager) getSystemService(AUDIO_SERVICE);
        alive = true; stopping = false;
        Thread t = new Thread(this::run, "zaxlet-aud");
        t.start();
        return START_NOT_STICKY;
    }

    private void run() {
        byte[] buf = new byte[1920];   // 10 ms
        try {
            android.os.Process.setThreadPriority(android.os.Process.THREAD_PRIORITY_URGENT_AUDIO);
            ws = new Ws("/aud/ws");   // sambung dulu: bila PC tidak terjangkau, speaker HP tidak sempat dibisukan
            ws.sock.setSendBufferSize(16384);   // buffer kirim kecil: jaringan tersendat = potongan lama dibuang AudioRecord, bukan menumpuk jadi detik tundaan
            rec.startRecording();
            mute(true);
            int tick = 0;
            while (!stopping) {
                int n = rec.read(buf, 0, buf.length);
                if (n < 0) break;
                if (n > 0) ws.sendBinary(buf, n);
                if (++tick % 50 == 0 && am.getStreamVolume(AudioManager.STREAM_MUSIC) != 0) am.setStreamVolume(AudioManager.STREAM_MUSIC, 0, 0);   // tombol volume tidak boleh membunyikan speaker
            }
        } catch (Throwable e) { if (!stopping) err = "Audio berhenti: " + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()); }
        shutdown();
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
        alive = false; stopping = true;
        try { if (ws != null) ws.close(); } catch (Exception e) { }
        try { if (rec != null) { rec.stop(); rec.release(); } } catch (Exception e) { }
        try { if (mp != null) mp.stop(); } catch (Exception e) { }
        if (am != null) mute(false);
        stopForeground(true);
        stopSelf();
    }

    @Override public void onDestroy() { shutdown(); super.onDestroy(); }
}
