package com.zaxlet.hp;

import android.app.*;
import android.content.Context;
import android.content.Intent;
import android.content.IntentFilter;
import android.content.pm.ServiceInfo;
import android.net.ConnectivityManager;
import android.net.Network;
import android.net.wifi.WifiManager;
import android.os.BatteryManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;

/**
 * Layanan latar depan: tetap tersambung ke PC lewat /ctl (LAN dulu, lalu Tailscale) walau layar mati.
 * Tahan putus: ping/pong dengan batas waktu (soket setengah mati ikut ditutup), sambung ulang seketika saat jaringan
 * berganti, dan jeda coba ulang naik bertahap. Dihidupkan lagi oleh BootReceiver / NotifService bila pengguna sudah mengaktifkannya.
 */
public class ConnService extends Service {
    static volatile String status = "Belum aktif";
    static volatile boolean alive;
    static volatile int state;   // 0 mati, 1 menyambung/menunggu, 2 tersambung
    private static volatile long lastTry;
    private Thread loop;
    private Cam cam;
    private PowerManager.WakeLock wl;
    private WifiManager.WifiLock wifi;
    private volatile Ws ctl;
    private static volatile Ws ctlWs;   // untuk NotifService
    private final Object wake = new Object();
    private boolean kick;
    private volatile long lastRx;
    private volatile boolean sawPong;   // PC lama tidak membalas ping: batas waktu baru berlaku setelah pong pertama
    private volatile boolean camType;
    private ConnectivityManager.NetworkCallback netCb;

    /** Kirim teks JSON ke PC lewat kanal kontrol; false bila belum tersambung. */
    static boolean send(String s) {
        Ws w = ctlWs;
        if (w == null) return false;
        try { w.sendText(s); return true; } catch (Exception e) { return false; }
    }
    static boolean connected() { Ws w = ctlWs; return w != null && !w.closed; }
    private volatile boolean stopping;

    /** Hidupkan layanan dari latar (boot, NotifService) bila pengguna pernah mengaktifkannya; tipe kamera tidak diminta. */
    static void ensure(Context c) {
        long now = System.currentTimeMillis();
        if (alive || now - lastTry < 20000) return;
        lastTry = now;
        try {
            Net.init(c);
            if (!Net.sp.getBoolean("auto", false) || !Net.paired()) return;
            c.startForegroundService(new Intent(c, ConnService.class).putExtra("bg", true));
        } catch (Exception e) { }
    }

    @Override public IBinder onBind(Intent i) { return null; }

    private int fgType(boolean camOk) {
        int t = Build.VERSION.SDK_INT >= 34 ? ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE : ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC;
        return camOk ? t | ServiceInfo.FOREGROUND_SERVICE_TYPE_CAMERA : t;
    }

    @Override public int onStartCommand(Intent in, int flags, int startId) {
        Net.init(this);
        if (in != null && "STOP".equals(in.getAction())) { Net.sp.edit().putBoolean("auto", false).apply(); shutdown(); return START_NOT_STICKY; }
        boolean bg = in != null && in.getBooleanExtra("bg", false);   // null = restart otomatis oleh sistem: juga dari latar
        if (alive) {
            if (!bg && !camType && Build.VERSION.SDK_INT >= 29) {   // pengguna membuka aplikasi: naikkan ke tipe kamera
                try { startForeground(1, note(status), fgType(true)); camType = true; } catch (Exception e) { }
            }
            return START_STICKY;
        }
        alive = true; stopping = false; self = this;
        if (!bg) Net.sp.edit().putBoolean("auto", true).apply();
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        nm.createNotificationChannel(new NotificationChannel("zaxlet", "Sambungan PC", NotificationManager.IMPORTANCE_LOW));
        Notification n = note("Menyambung ke PC…");
        camType = false;
        try {
            if (Build.VERSION.SDK_INT >= 29) {
                try { startForeground(1, n, fgType(!bg)); camType = !bg; }
                catch (Exception e) { startForeground(1, n, fgType(false)); }   // dari latar Android melarang tipe kamera
            } else startForeground(1, n);
        } catch (Exception e) { alive = false; stopSelf(); return START_NOT_STICKY; }

        wl = ((PowerManager) getSystemService(POWER_SERVICE)).newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "zaxlet:conn");
        wl.acquire();
        wifi = ((WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE)).createWifiLock(Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : WifiManager.WIFI_MODE_FULL_HIGH_PERF, "zaxlet:wifi");
        wifi.acquire();
        cam = new Cam(this);
        watchNetwork();
        loop = new Thread(this::run, "zaxlet-ctl");
        loop.start();
        return START_STICKY;
    }

    /** Ganti jaringan (Wi-Fi <-> data, Wi-Fi lain): buang sambungan lama dan coba lagi sekarang, bukan menunggu timeout. */
    private void watchNetwork() {
        try {
            ConnectivityManager cm = (ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE);
            netCb = new ConnectivityManager.NetworkCallback() {
                boolean first = true;
                @Override public void onAvailable(Network n) { boolean changed = !first; first = false; if (changed) { Net.active = null; kick(); } }
            };
            cm.registerDefaultNetworkCallback(netCb);
        } catch (Exception e) { netCb = null; }
    }

    private static volatile ConnService self;
    /** Sambung ulang sekarang (mis. setelah pasangan diganti), tanpa menunggu jeda coba ulang. */
    static void kickNow() { ConnService s = self; if (s != null) s.kick(); }

    private void kick() {
        Ws w = ctl;
        if (w != null) w.close();
        synchronized (wake) { kick = true; wake.notifyAll(); }
    }

    private void pause(int ms) {
        synchronized (wake) {
            if (!kick && !stopping) try { wake.wait(ms); } catch (InterruptedException e) { }
            kick = false;
        }
    }

    private Notification note(String text) {
        Intent stop = new Intent(this, ConnService.class).setAction("STOP");
        PendingIntent pi = PendingIntent.getService(this, 0, stop, PendingIntent.FLAG_IMMUTABLE);
        PendingIntent open = PendingIntent.getActivity(this, 1, new Intent(this, MainActivity.class), PendingIntent.FLAG_IMMUTABLE);
        return new Notification.Builder(this, "zaxlet").setSmallIcon(android.R.drawable.ic_menu_camera).setContentTitle("ZaxLet HP")
                .setContentText(text).setOngoing(true).setContentIntent(open)
                .addAction(new Notification.Action.Builder(null, "Matikan", pi).build()).build();
    }

    private void setStatus(String s, int st) {
        status = s; state = st;
        ((NotificationManager) getSystemService(NOTIFICATION_SERVICE)).notify(1, note(s));
    }

    /** {"t":"b","l":persen,"c":1 bila mengisi}; null bila tidak terbaca. */
    private String battery() {
        Intent b = registerReceiver(null, new IntentFilter(Intent.ACTION_BATTERY_CHANGED));
        if (b == null) return null;
        int lv = b.getIntExtra(BatteryManager.EXTRA_LEVEL, -1), sc = b.getIntExtra(BatteryManager.EXTRA_SCALE, 100), st = b.getIntExtra(BatteryManager.EXTRA_STATUS, -1);
        if (lv < 0 || sc <= 0) return null;
        boolean ch = st == BatteryManager.BATTERY_STATUS_CHARGING || st == BatteryManager.BATTERY_STATUS_FULL;
        return "{\"t\":\"b\",\"l\":" + (lv * 100 / sc) + ",\"c\":" + (ch ? 1 : 0) + "}";
    }

    private void run() {
        int backoff = 1000;
        while (!stopping) {
            boolean got = false;
            Ws w = null;
            try {
                if (!Net.paired()) { setStatus("Belum dipasangkan", 1); pause(3000); continue; }
                setStatus("Menyambung ke PC…", 1);
                w = new Ws("/ctl");
                ctl = w; ctlWs = w; got = true; backoff = 1000;
                lastRx = System.currentTimeMillis(); sawPong = false;
                NotifService.resync();   // kirim ulang notifikasi aktif ke PC
                setStatus("Tersambung ke PC (" + Net.active + ")", 2);
                final Ws mine = w;
                Thread ping = new Thread(() -> {
                    try {
                        String sentBat = "";
                        while (!mine.closed) {
                            String bat = battery();   // level baterai menumpang di siklus ping; PC lama mengabaikan jenis pesan yang tidak dikenal
                            if (bat != null && !bat.equals(sentBat)) { mine.sendText(bat); sentBat = bat; }
                            Thread.sleep(10000);
                            if (sawPong && System.currentTimeMillis() - lastRx > 30000) { mine.close(); break; }   // PC diam 30 dtk = putus
                            mine.sendText("ping");
                        }
                    } catch (Exception e) { mine.close(); }
                });
                ping.setDaemon(true);
                ping.start();
                String m;
                while (!stopping && (m = w.readText()) != null) {
                    lastRx = System.currentTimeMillis();
                    if (m.equals("pong")) sawPong = true;
                    else if (!m.isEmpty()) handle(m);
                }
                if (!stopping) setStatus("Terputus, menyambung ulang…", 1);
            } catch (Exception e) {
                if (!stopping) setStatus("Menunggu PC… (" + (e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage()) + ")", 1);
            } finally {
                if (w != null) w.close();
                if (ctl == w) ctl = null;
                if (ctlWs == w) ctlWs = null;
                if (cam != null && cam.running()) cam.stop();   // putus dari PC = kamera ikut mati
            }
            if (stopping) break;
            pause(got ? 1000 : backoff);
            if (!got) backoff = Math.min(backoff * 2, 30000);
        }
    }

    private static final java.util.concurrent.atomic.AtomicInteger fileNotifId = new java.util.concurrent.atomic.AtomicInteger(100);

    private static android.net.Uri parseSingleHttpUrl(String text) {
        if (text == null) return null;
        String s = text.trim();
        if (s.isEmpty() || s.length() > 2000) return null;
        for (int i = 0; i < s.length(); i++) {
            if (Character.isWhitespace(s.charAt(i))) return null;
        }
        try {
            new java.net.URI(s);
            android.net.Uri uri = android.net.Uri.parse(s);
            String scheme = uri.getScheme();
            String host = uri.getHost();
            if (scheme == null || (!scheme.equalsIgnoreCase("http") && !scheme.equalsIgnoreCase("https"))) return null;
            if (host == null || host.isEmpty()) return null;
            return uri;
        } catch (Exception e) {
            return null;
        }
    }

    private static android.net.Uri parsePhoneNumber(String text) {
        if (text == null) return null;
        String s = text.trim();
        if (s.isEmpty() || s.length() > 32) return null;
        StringBuilder norm = new StringBuilder(s.length());
        int digits = 0;
        for (int i = 0; i < s.length(); i++) {
            char ch = s.charAt(i);
            if (ch == '+') {
                if (i != 0) return null;
                norm.append(ch);
            } else if (ch >= '0' && ch <= '9') {
                digits++;
                norm.append(ch);
            } else if (ch != ' ' && ch != '-') {
                return null;
            }
        }
        if (digits < 6 || digits > 16) return null;
        return android.net.Uri.parse("tel:" + norm.toString());
    }

    /** Teks dari PC: masuk clipboard HP dan tampil di notifikasi (cadangan bila Android menolak penulisan dari latar). */
    private void fromPc(String b64) {
        final String t;
        try {
            t = new String(android.util.Base64.decode(b64, android.util.Base64.DEFAULT), "UTF-8");
        } catch (Exception e) { return; }
        new android.os.Handler(android.os.Looper.getMainLooper()).post(() -> {
            try {
                ((android.content.ClipboardManager) getSystemService(CLIPBOARD_SERVICE)).setPrimaryClip(android.content.ClipData.newPlainText("ZaxLet", t));
            } catch (Exception e) { }
        });
        try {
            NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
            nm.createNotificationChannel(new NotificationChannel("zaxlet-text", "Teks dari PC", NotificationManager.IMPORTANCE_DEFAULT));
            String head = t.length() > 80 ? t.substring(0, 80) + "..." : t;
            Notification.Builder nb = new Notification.Builder(this, "zaxlet-text").setSmallIcon(android.R.drawable.ic_menu_edit).setContentTitle("Teks dari PC disalin")
                    .setContentText(head).setStyle(new Notification.BigTextStyle().bigText(t.length() > 1000 ? t.substring(0, 1000) + "..." : t)).setAutoCancel(true);
            android.net.Uri url = parseSingleHttpUrl(t);
            if (url != null) {
                Intent view = new Intent(Intent.ACTION_VIEW, url).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                PendingIntent open = PendingIntent.getActivity(this, 2, view, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
                nb.setContentIntent(open).addAction(new Notification.Action.Builder(null, "Buka", open).build());
            } else {
                android.net.Uri tel = parsePhoneNumber(t);
                if (tel != null) {
                    Intent dial = new Intent(Intent.ACTION_DIAL, tel).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    PendingIntent call = PendingIntent.getActivity(this, 2, dial, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
                    nb.setContentIntent(call).addAction(new Notification.Action.Builder(null, "Telepon", call).build());
                }
            }
            nm.notify(2, nb.build());
        } catch (Exception e) { }
    }

    private void receiveFile(int idx, String rawName) {
        new Thread(() -> {
            try {
                Dl.Result r = Dl.download(this, idx, rawName);
                NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
                nm.createNotificationChannel(new NotificationChannel("zaxlet-file", "Berkas dari PC", NotificationManager.IMPORTANCE_DEFAULT));
                Intent view = new Intent(Intent.ACTION_VIEW);
                int dot = r.name.lastIndexOf('.');
                String ext = dot >= 0 && dot < r.name.length() - 1 ? r.name.substring(dot + 1).toLowerCase(java.util.Locale.ROOT) : "";
                String mime = ext.isEmpty() ? null : android.webkit.MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext);
                if (mime != null && !mime.isEmpty()) view.setDataAndType(r.uri, mime);
                else view.setData(r.uri);
                view.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_GRANT_READ_URI_PERMISSION);
                int id = fileNotifId.incrementAndGet();
                PendingIntent open = PendingIntent.getActivity(this, id, view, PendingIntent.FLAG_IMMUTABLE | PendingIntent.FLAG_UPDATE_CURRENT);
                Notification n = new Notification.Builder(this, "zaxlet-file")
                        .setSmallIcon(android.R.drawable.stat_sys_download_done)
                        .setContentTitle("Diterima dari PC: " + r.name)
                        .setContentText(r.where)
                        .setContentIntent(open)
                        .setAutoCancel(true)
                        .build();
                nm.notify(id, n);
            } catch (Exception e) {
                Net.active = null;
            }
        }, "zaxlet-dl").start();
    }

    private void handle(String m) {
        if (m.startsWith("text ")) { fromPc(m.substring(5)); return; }
        if (m.startsWith("file ")) {
            String[] q = m.split(" ", 3);
            if (q.length == 3) {
                try {
                    int idx = Integer.parseInt(q[1]);
                    String name = new String(android.util.Base64.decode(q[2], android.util.Base64.DEFAULT), "UTF-8");
                    receiveFile(idx, name);
                } catch (Exception e) { }
            }
            return;
        }
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
            if (p[1].equals("off")) { cam.stop(); setStatus("Tersambung ke PC (" + Net.active + ")", 2); }
            else if (p[1].equals("on")) {
                int h = p.length > 2 ? Integer.parseInt(p[2]) : 720, f = p.length > 3 ? Integer.parseInt(p[3]) : 15;
                cam.start(h, f, p.length > 4 && p[4].equals("front"));
                setStatus("Kamera menyala → PC", 2);
            }
        }
    }

    private void shutdown() {
        stopping = true; alive = false; self = null; status = "Belum aktif"; state = 0;
        try { if (netCb != null) ((ConnectivityManager) getSystemService(CONNECTIVITY_SERVICE)).unregisterNetworkCallback(netCb); } catch (Exception e) { }
        netCb = null;
        synchronized (wake) { wake.notifyAll(); }
        try { if (ctl != null) ctl.close(); } catch (Exception e) { }
        ctlWs = null;
        if (cam != null) cam.stop();
        if (wl != null && wl.isHeld()) wl.release();
        if (wifi != null && wifi.isHeld()) wifi.release();
        stopForeground(true);
        stopSelf();
    }

    @Override public void onDestroy() { if (alive) shutdown(); super.onDestroy(); }
}
