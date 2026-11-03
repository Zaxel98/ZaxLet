package com.zaxlet.hp;

import android.app.*;
import android.content.Context;
import android.content.Intent;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

/**
 * Layanan latar depan: tetap tersambung ke PC lewat /ctl (LAN dulu, lalu Tailscale) walau layar mati.
 * Layanan harus dinyalakan sekali dari layar aplikasi (syarat Android untuk akses kamera dari layanan latar depan).
 */
public class ConnService extends Service {
    static volatile String status = "Belum aktif";
    static volatile boolean alive;
    private Thread loop;
    private Cam cam;
    private PowerManager.WakeLock wl;
    private WifiManager.WifiLock wifi;
    private volatile Ws ctl;
    private static volatile Ws ctlWs;   // untuk NotifService

    /** Kirim teks JSON ke PC lewat kanal kontrol; false bila belum tersambung. */
    static boolean send(String s) {
        Ws w = ctlWs;
        if (w == null) return false;
        try { w.sendText(s); return true; } catch (Exception e) { return false; }
    }
    static boolean connected() { Ws w = ctlWs; return w != null && !w.closed; }
    private volatile boolean stopping;

    @Override public IBinder onBind(Intent i) { return null; }

    @Override public int onStartCommand(Intent in, int flags, int startId) {
        Net.init(this);
        if (in != null && "STOP".equals(in.getAction())) { shutdown(); return START_NOT_STICKY; }
        if (alive) return START_STICKY;
        alive = true; stopping = false;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        nm.createNotificationChannel(new NotificationChannel("zaxlet", "Sambungan PC", NotificationManager.IMPORTANCE_LOW));
        Notification n = note("Menyambung ke PC…");
        try {
            if (Build.VERSION.SDK_INT >= 29)
                startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA | ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
            else startForeground(1, n);
        } catch (Exception e) {
            // dimulai dari latar (mis. restart otomatis): Android melarang tipe kamera, jadi hanya sambungan/berkas yang jalan
            startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC);
        }

        wl = ((PowerManager) getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "zaxlet:conn");
        wl.acquire();
        wifi = ((WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE)).createWifiLock(Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : WifiManager.WIFI_MODE_FULL_HIGH_PERF, "zaxlet:wifi");
        wifi.acquire();
        cam = new Cam(this);
        loop = new Thread(this::run, "zaxlet-ctl");
        loop.start();
        return START_STICKY;
    }

    private Notification note(String text) {
        Intent stop = new Intent(this, ConnService.class).setAction("STOP");
        PendingIntent pi = PendingIntent.getService(this, 0, stop, PendingIntent.FLAG_IMMUTABLE);
        PendingIntent open = PendingIntent.getActivity(this, 1, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, "zaxlet").setSmallIcon(android.R.drawable.ic_menu_camera).setContentTitle("ZaxLet HP")
                .setContentText(text).setOngoing(true).setContentIntent(open)
                .addAction(new Notification.Action.Builder(null, "Matikan", pi).build()).build();
    }

    private void setStatus(String s) {
        status = s;
        ((NotificationManager) getSystemService(NOTIFICATION_SERVICE)).notify(1, note(s));
    }

    private void run() {
        while (!stopping) {
            try {
                if (!Net.paired()) { setStatus("Belum dipasangkan"); Thread.sleep(3000); continue; }
                Ws w = new Ws("/ctl");
                ctl = w; ctlWs = w;
                NotifService.resync();   // kirim ulang notifikasi aktif ke PC
                setStatus("Tersambung ke PC (" + Net.active + ")");
                final Ws mine = w;
                Thread ping = new Thread(() -> {
                    try { while (!mine.closed) { Thread.sleep(15000); mine.sendText("ping"); } } catch (Exception e) { mine.close(); }
                });
                ping.setDaemon(true);
                ping.start();
                String m;
                while (!stopping && (m = w.readText()) != null) if (!m.isEmpty()) handle(m);
                w.close(); ctlWs = null;
            } catch (Exception e) {
                ctlWs = null;
                setStatus("Menunggu PC… (" + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()) + ")");
            } finally {
                if (cam != null && cam.running()) cam.stop();   // putus dari PC = kamera ikut mati
            }
            try { if (!stopping) Thread.sleep(3000); } catch (InterruptedException e) { }
        }
    }

    private void handle(String m) {
        if (m.startsWith("reply ")) {   // reply <base64 key> <base64 teks>; key notifikasi Android memuat "|" jadi dikodekan
            String[] q = m.split(" ", 3);
            if (q.length == 3) {
                try {
                    String key = new String(android.util.Base64.decode(q[1], android.util.Base64.DEFAULT), "UTF-8");
                    String txt = new String(android.util.Base64.decode(q[2], android.util.Base64.DEFAULT), "UTF-8");
                    NotifService.reply(key, txt);
                } catch (Exception e) { NotifService.lastErr = "balas gagal: " + e; }
            }
            return;
        }
        String[] p = m.split(" ");
        if (p.length >= 2 && p[0].equals("cam")) {
            if (p[1].equals("off")) { cam.stop(); setStatus("Tersambung ke PC (" + Net.active + ")"); }
            else if (p[1].equals("on")) {
                int h = p.length > 2 ? Integer.parseInt(p[2]) : 720, f = p.length > 3 ? Integer.parseInt(p[3]) : 15;
                cam.start(h, f, p.length > 4 && p[4].equals("front"));
                setStatus("Kamera menyala → PC");
            }
        }
    }

    private void shutdown() {
        stopping = true; alive = false; status = "Belum aktif";
        try { if (ctl != null) ctl.close(); } catch (Exception e) { }
        if (cam != null) cam.stop();
        if (wl != null && wl.isHeld()) wl.release();
        if (wifi != null && wifi.isHeld()) wifi.release();
        stopForeground(true);
        stopSelf();
    }

    @Override public void onDestroy() { if (alive) shutdown(); super.onDestroy(); }
}
