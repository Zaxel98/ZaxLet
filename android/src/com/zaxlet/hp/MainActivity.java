package com.zaxlet.hp;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ContentValues;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.net.Uri;
import android.os.*;
import android.provider.MediaStore;
import android.provider.OpenableColumns;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.*;
import java.io.*;
import java.util.ArrayList;
import javax.net.ssl.HttpsURLConnection;

/** Main screen: connection status, permission list to stay alive in background, pair, send files/text to PC, fetch files from Shelf. Also Share menu receiver. */
public class MainActivity extends Activity {
    private static final class Check { View dot; TextView label; Button act; boolean ok; }

    private View stDot;
    private TextView stTitle, stDetail, stDiag;
    private Button toggle, audBtn, scrBtn, accBtn;
    private TextView audNote, accStatus;
    private EditText msg;
    private Check cNotif, cBattery, cPost;
    private boolean autoAudTried, inAudPrompt, pendingVideo;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final Runnable tick = new Runnable() { public void run() { refresh(); ui.postDelayed(this, 1500); } };

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        Net.init(this);
        LinearLayout root = Ui.page(this, "ZaxLet HP");

        // ---- status ----
        LinearLayout sc = Ui.card(root, null);
        LinearLayout head = new LinearLayout(this);
        head.setGravity(Gravity.CENTER_VERTICAL);
        stDot = Ui.dot(this, Ui.DIM);
        head.addView(stDot, new LinearLayout.LayoutParams(Ui.dp(this, 12), Ui.dp(this, 12)));
        stTitle = Ui.text(this, "", 18, Ui.TEXT, true);
        LinearLayout.LayoutParams tp = new LinearLayout.LayoutParams(0, -2, 1f);
        tp.leftMargin = Ui.dp(this, 10);
        head.addView(stTitle, tp);
        toggle = Ui.button(this, "", true, v -> {
            if (ConnService.alive) startService(new Intent(this, ConnService.class).setAction("STOP")); else startService();
            ui.postDelayed(this::refresh, 400);
        });
        small(toggle);
        head.addView(toggle, new LinearLayout.LayoutParams(-2, -2));
        sc.addView(head);
        LinearLayout info = new LinearLayout(this);   // address text on the left, small QR on the right (below the Stop button)
        info.setGravity(Gravity.CENTER_VERTICAL);
        LinearLayout txt = new LinearLayout(this);
        txt.setOrientation(LinearLayout.VERTICAL);
        stDetail = Ui.text(this, "", 14, Ui.DIM, false);
        stDiag = Ui.text(this, "", 12, Ui.DIM, false);
        txt.addView(stDetail);
        txt.addView(stDiag, Ui.gap(this, 2));
        LinearLayout.LayoutParams xp = new LinearLayout.LayoutParams(0, -2, 1f);
        xp.rightMargin = Ui.dp(this, 8);
        info.addView(txt, xp);
        info.addView(small(Ui.button(this, "QR", false, v -> startActivity(new Intent(this, ScanActivity.class)))), new LinearLayout.LayoutParams(-2, -2));   // pair: scan QR from ZaxLet
        sc.addView(info, Ui.gap(this, 6));

        // ---- actions (most frequently used: right below status) ----
        LinearLayout ac = Ui.card(root, "Kirim & ambil");
        LinearLayout r1 = new LinearLayout(this);
        r1.addView(Ui.button(this, "Kirim file", false, v -> pickFiles()), new LinearLayout.LayoutParams(0, -2, 1f));
        LinearLayout.LayoutParams sp = new LinearLayout.LayoutParams(0, -2, 1f);
        sp.leftMargin = Ui.dp(this, 8);
        r1.addView(Ui.button(this, "Ambil dari Shelf", false, v -> shelf()), sp);
        ac.addView(r1);
        LinearLayout r2 = new LinearLayout(this);
        r2.setGravity(Gravity.CENTER_VERTICAL);
        msg = Ui.field(this, "Teks ke clipboard PC", 1);
        r2.addView(msg, new LinearLayout.LayoutParams(0, Ui.dp(this, 48), 1f));
        LinearLayout.LayoutParams kp = new LinearLayout.LayoutParams(-2, -2);
        kp.leftMargin = Ui.dp(this, 8);
        r2.addView(small(Ui.button(this, "Kirim", true, v -> sendText(msg.getText().toString(), () -> msg.setText("")))), kp);
        ac.addView(r2, Ui.gap(this, 8));
        ac.addView(Ui.button(this, "Notifikasi ke PC", false, v -> startActivity(new Intent(this, AppsActivity.class))), Ui.gap(this, 8));
        audBtn = Ui.button(this, "", false, v -> toggleAudio(false));
        ac.addView(audBtn, Ui.gap(this, 8));
        scrBtn = Ui.button(this, "", false, v -> toggleAudio(true));
        ac.addView(scrBtn, Ui.gap(this, 8));
        LinearLayout accRow = new LinearLayout(this);
        accRow.setGravity(Gravity.CENTER_VERTICAL);
        accStatus = Ui.text(this, "", 13, Ui.DIM, false);
        accRow.addView(accStatus, new LinearLayout.LayoutParams(0, -2, 1f));
        accBtn = small(Ui.button(this, "Aktifkan", false, v -> startActivity(new Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))));
        accRow.addView(accBtn, new LinearLayout.LayoutParams(-2, -2));
        ac.addView(accRow, Ui.gap(this, 8));
        Switch autoAud = new Switch(this);
        autoAud.setText("Mulai audio otomatis saat aplikasi dibuka");
        autoAud.setTextColor(Ui.TEXT);
        autoAud.setTextSize(14);
        autoAud.setChecked(Net.sp.getBoolean("autoaud", false));
        autoAud.setOnCheckedChangeListener((btn, on) -> Net.sp.edit().putBoolean("autoaud", on).apply());
        ac.addView(autoAud, Ui.gap(this, 8));
        audNote = Ui.text(this, "", 12, Ui.DIM, false);
        ac.addView(audNote, Ui.gap(this, 4));

        // ---- permissions to stay alive (set once, so at the very bottom) ----
        LinearLayout kc = Ui.card(root, "Agar tetap hidup di latar");
        cNotif = check(kc, "Akses notifikasi", v -> notifAccess());
        cBattery = check(kc, "Bebas hemat baterai", v -> batteryOpt());
        cPost = check(kc, "Izin notifikasi aplikasi", v -> requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS}, 8));
        View auto = check(kc, "Autostart / kunci aplikasi (Xiaomi, Oppo, Vivo, Huawei)", v -> autostart()).dot;
        Ui.tint(auto, Ui.DIM);
        TextView kh = Ui.text(this, "Aplikasi hidup lagi sendiri setelah HP restart. Autostart perlu dicek manual di merek tertentu; Android tidak bisa membacanya dari aplikasi.", 12, Ui.DIM, false);
        kc.addView(kh, Ui.gap(this, 8));
        handle(getIntent());
    }

    /** Small button (44dp height, wraps text width) for narrow slots next to text or input fields. */
    private Button small(Button b) {
        b.setTextSize(14); b.setMinWidth(0); b.setMinimumWidth(0);
        b.setMinHeight(Ui.dp(this, 44)); b.setMinimumHeight(Ui.dp(this, 44));
        b.setPadding(Ui.dp(this, 18), 0, Ui.dp(this, 18), 0);
        return b;
    }

    private Check check(LinearLayout parent, String label, View.OnClickListener act) {
        Check c = new Check();
        LinearLayout row = new LinearLayout(this);
        row.setGravity(Gravity.CENTER_VERTICAL);
        c.dot = Ui.dot(this, Ui.DIM);
        row.addView(c.dot, new LinearLayout.LayoutParams(Ui.dp(this, 10), Ui.dp(this, 10)));
        c.label = Ui.text(this, label, 15, Ui.TEXT, false);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
        lp.leftMargin = Ui.dp(this, 12); lp.rightMargin = Ui.dp(this, 8);
        row.addView(c.label, lp);
        c.act = new Button(this);
        c.act.setText("Atur"); c.act.setAllCaps(false); c.act.setTextColor(Ui.ACC); c.act.setTextSize(14);
        c.act.setBackground(null); c.act.setStateListAnimator(null); c.act.setMinHeight(Ui.dp(this, 48)); c.act.setMinimumWidth(Ui.dp(this, 56));
        c.act.setOnClickListener(act);
        row.addView(c.act);
        parent.addView(row);
        return c;
    }

    private void mark(Check c, boolean ok) {
        c.ok = ok;
        Ui.tint(c.dot, ok ? Ui.ACC : Ui.WARN);
        c.act.setVisibility(ok ? View.GONE : View.VISIBLE);
    }

    @Override protected void onNewIntent(Intent i) { super.onNewIntent(i); handle(i); }
    @Override protected void onResume() {
        super.onResume();
        ui.post(tick);
        if (!autoAudTried && Net.sp.getBoolean("autoaud", false) && Net.paired() && !AudioService.alive && Build.VERSION.SDK_INT >= 29) {
            autoAudTried = true;
            toggleAudio(false);
        }
    }
    @Override protected void onPause() { super.onPause(); ui.removeCallbacks(tick); }
    @Override protected void onStop() { super.onStop(); if (!inAudPrompt) autoAudTried = false; }

    private boolean listenerOn() {
        String l = Settings.Secure.getString(getContentResolver(), "enabled_notification_listeners");
        return l != null && l.contains(getPackageName());
    }

    private void refresh() {
        boolean paired = Net.paired(), alive = ConnService.alive;
        int color; String title, detail;
        if (!paired) { color = Ui.WARN; title = "Belum dipasangkan"; detail = "Ketuk QR lalu pindai kode pasangan di ZaxLet PC."; }
        else if (!alive) { color = Ui.DIM; title = "Layanan mati"; detail = "Aktifkan agar notifikasi dan balasan chat terus tersambung."; }
        else if (ConnService.state == 2) { color = Ui.ACC; title = "Tersambung"; detail = "Ke PC lewat " + Net.active; }
        else {
            color = Ui.WARN; title = "Menunggu PC…"; detail = ConnService.status;
            if (detail.contains("sidik jari")) detail = "Sertifikat PC tidak cocok dengan pasangan ini. Ketuk QR dan pindai ulang dari ZaxLet PC.";
        }
        Ui.tint(stDot, color);
        stTitle.setText(title); stDetail.setText(detail);
        boolean on = listenerOn();
        stDiag.setText(on ? "Notifikasi: dilihat " + NotifService.seen + ", terkirim " + NotifService.sent + (NotifService.lastErr.isEmpty() ? "" : " (" + NotifService.lastErr + ")") : "");
        stDiag.setVisibility(on ? View.VISIBLE : View.GONE);
        audBtn.setText(AudioService.alive ? "Matikan audio ke PC" : "Audio HP ke PC");
        scrBtn.setText(AudioService.alive ? "Matikan layar + audio" : "Layar + audio HP ke PC");
        audNote.setText(AudioService.alive ? (AudioService.videoActive ? "Layar + audio HP sedang dikirim ke PC." : "Speaker HP senyap, suara diputar di PC bersama audio lain.") : AudioService.err);
        audNote.setVisibility(audNote.getText().length() == 0 ? View.GONE : View.VISIBLE);
        boolean accOn = ZaxAccess.instance != null;
        accStatus.setText("Kontrol dari PC: " + (accOn ? "aktif" : "belum aktif"));
        accStatus.setTextColor(accOn ? Ui.ACC : Ui.DIM);
        accBtn.setVisibility(accOn ? View.GONE : View.VISIBLE);
        toggle.setText(alive ? "Matikan" : "Aktifkan");
        toggle.setBackground(Ui.shape(this, alive ? Ui.FIELD : Ui.ACC, 14));
        toggle.setTextColor(alive ? Ui.TEXT : Ui.ON_ACC);

        mark(cNotif, on);
        mark(cBattery, ((PowerManager) getSystemService(POWER_SERVICE)).isIgnoringBatteryOptimizations(getPackageName()));
        mark(cPost, Build.VERSION.SDK_INT < 33 || checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED);
    }

    /** Android 13+ blocks notification access for sideloaded APKs ("restricted settings"); explain the workaround. */
    private void notifAccess() {
        final Intent listener = new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS);
        if (Build.VERSION.SDK_INT < 33) { startActivity(listener); return; }
        new AlertDialog.Builder(this).setTitle("Akses notifikasi")
                .setMessage("1. Ketuk \"Buka akses notifikasi\" lalu nyalakan ZaxLet HP.\n\n"
                        + "2. Jika Android menolak (\"setelan terbatas\"): ketuk \"Info aplikasi\", buka menu ⋮ di pojok kanan atas, pilih \"Izinkan setelan terbatas\", lalu ulangi langkah 1. Menu itu baru muncul setelah langkah 1 pernah ditolak.")
                .setPositiveButton("Buka akses notifikasi", (d, w) -> startActivity(listener))
                .setNeutralButton("Info aplikasi", (d, w) -> startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName()))))
                .show();
    }

    /** Manufacturer's built-in autostart screen; no official API, so try several and fall back to App Info. */
    private void autostart() {
        String[][] c = {
            {"com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"},
            {"com.coloros.safecenter", "com.coloros.safecenter.startupapp.StartupAppListActivity"},
            {"com.oppo.safe", "com.oppo.safe.permission.startup.StartupAppListActivity"},
            {"com.vivo.permissionmanager", "com.vivo.permissionmanager.activity.BgStartUpManagerActivity"},
            {"com.huawei.systemmanager", "com.huawei.systemmanager.startupmgr.ui.StartupNormalAppListActivity"}};
        for (String[] p : c) {
            try { startActivity(new Intent().setClassName(p[0], p[1])); return; } catch (Exception e) { }
        }
        toast("Cari \"Autostart\" atau \"Aplikasi tidur\" di Info aplikasi → Baterai");
        startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:" + getPackageName())));
    }

    private void handle(Intent i) {
        if (i == null) return;
        String a = i.getAction();
        if (Intent.ACTION_VIEW.equals(a) && i.getData() != null) pair(i.getData().toString());
        else if (Intent.ACTION_SEND.equals(a)) {
            Uri u = i.getParcelableExtra(Intent.EXTRA_STREAM);
            if (u != null) { ArrayList<Uri> l = new ArrayList<>(); l.add(u); upload(l); }
            else { CharSequence t = i.getCharSequenceExtra(Intent.EXTRA_TEXT); if (t != null) sendText(t.toString(), null); }   // Share text from another app
        } else if (Intent.ACTION_SEND_MULTIPLE.equals(a)) {
            ArrayList<Uri> l = i.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (l != null) upload(l);
        }
    }

    /** Text to PC clipboard via control channel. PC frame limit is 64 KB, so keep the JSON below that. */
    private void sendText(String s, Runnable onSent) {
        if (s == null || s.trim().isEmpty()) { toast("Teks kosong"); return; }
        if (!ConnService.connected()) { toast("Belum tersambung ke PC: aktifkan layanan latar belakang"); return; }
        final String j;
        try {
            org.json.JSONObject o = new org.json.JSONObject();
            o.put("t", "x"); o.put("text", s);
            j = o.toString();
            if (j.getBytes("UTF-8").length > 60000) { toast("Teks terlalu panjang (maksimal sekitar 60 KB)"); return; }
        } catch (Exception e) { toast("Gagal mengirim, cek sambungan ke PC"); return; }
        new Thread(() -> {
            boolean ok = ConnService.send(j);
            ui.post(() -> {
                if (ok) {
                    if (onSent != null) onSent.run();
                    toast("Teks terkirim ke PC");
                } else {
                    toast("Gagal mengirim, cek sambungan ke PC");
                }
            });
        }, "zaxlet-text").start();
    }

    /** Open Google Lens to scan QR from ZaxLet PC; tap the result (zaxlet://pair) and this app opens automatically. Fallback: camera app. */
    /** Built-in camera only copies the link: when this screen regains focus and clipboard has a new pair link, pair automatically. */
    @Override public void onWindowFocusChanged(boolean focus) {
        super.onWindowFocusChanged(focus);
        if (!focus) return;
        try {
            android.content.ClipData d = ((android.content.ClipboardManager) getSystemService(CLIPBOARD_SERVICE)).getPrimaryClip();
            CharSequence t = d == null || d.getItemCount() == 0 ? null : d.getItemAt(0).getText();
            String s = t == null ? "" : t.toString().trim();
            if (s.startsWith("zaxlet://pair") && !s.equals(Net.get("clip"))) { Net.sp.edit().putString("clip", s).apply(); pair(s); }
        } catch (Exception e) { }
    }

    private void pair(String s) {
        if (Net.savePairing(s)) {
            if (ConnService.alive) { ConnService.kickNow(); toast("Terpasang, menyambung ke PC…"); }
            else toast("Terpasang. Ketuk \"Aktifkan\".");
        } else toast("Tautan tidak valid");
        refresh();
    }

    private void startService() {
        if (!Net.paired()) { toast("Pasangkan dulu dengan ZaxLet di PC"); return; }
        ArrayList<String> need = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) need.add(Manifest.permission.CAMERA);
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) need.add(Manifest.permission.POST_NOTIFICATIONS);
        if (!need.isEmpty()) { requestPermissions(need.toArray(new String[0]), 7); return; }
        startForegroundService(new Intent(this, ConnService.class));
        toast("Layanan diaktifkan");
    }

    @Override public void onRequestPermissionsResult(int c, String[] p, int[] r) {
        if (c == 7) {
            boolean denied = r.length == 0;
            for (int g : r) if (g != PackageManager.PERMISSION_GRANTED) { denied = true; break; }
            if (Net.paired()) {
                // Start directly instead of calling startService() so denied permissions don't re-trigger requestPermissions.
                startForegroundService(new Intent(this, ConnService.class));
                toast(denied ? "Layanan diaktifkan (sebagian izin ditolak)" : "Layanan diaktifkan");
            }
        } else if (c == 10) {
            inAudPrompt = false;
            if (r.length > 0 && r[0] == PackageManager.PERMISSION_GRANTED) toggleAudio(pendingVideo);
        }
    }

    /** Phone audio (and optional screen) -> PC: audio record permission, then Android screen capture dialog; result is forwarded to AudioService. */
    private void toggleAudio(boolean video) {
        if (AudioService.alive) { startService(new Intent(this, AudioService.class).setAction("STOP")); ui.postDelayed(this::refresh, 400); return; }
        if (!Net.paired()) { toast("Pasangkan dulu dengan ZaxLet di PC"); return; }
        if (Build.VERSION.SDK_INT < 29) { toast("Butuh Android 10 ke atas"); return; }
        pendingVideo = video;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) { inAudPrompt = true; requestPermissions(new String[]{Manifest.permission.RECORD_AUDIO}, 10); return; }
        inAudPrompt = true;
        startActivityForResult(((android.media.projection.MediaProjectionManager) getSystemService(MEDIA_PROJECTION_SERVICE)).createScreenCaptureIntent(), 10);
    }

    private void batteryOpt() {
        try { startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))); }
        catch (Exception e) { startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); }
    }

    private void pickFiles() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        startActivityForResult(i, 9);
    }

    @Override protected void onActivityResult(int rc, int res, Intent d) {
        if (rc == 10) {
            inAudPrompt = false;
            if (res == RESULT_OK && d != null) startForegroundService(new Intent(this, AudioService.class).putExtra("rc", res).putExtra("data", d).putExtra("video", pendingVideo));
            ui.postDelayed(this::refresh, 600);
            return;
        }
        if (rc != 9 || res != RESULT_OK || d == null) return;
        ArrayList<Uri> l = new ArrayList<>();
        if (d.getClipData() != null) { ClipData c = d.getClipData(); for (int i = 0; i < c.getItemCount(); i++) l.add(c.getItemAt(i).getUri()); }
        else if (d.getData() != null) l.add(d.getData());
        upload(l);
    }

    private void upload(ArrayList<Uri> uris) {
        if (!Net.paired()) { toast("Pasangkan dulu dengan ZaxLet di PC"); return; }
        toast("Mengirim " + uris.size() + " file…");
        new Thread(() -> {
            int ok = 0;
            for (Uri u : uris) {
                try {
                    String name = "file"; long size = -1;
                    try (Cursor c = getContentResolver().query(u, null, null, null, null)) {
                        if (c != null && c.moveToFirst()) {
                            int n = c.getColumnIndex(OpenableColumns.DISPLAY_NAME), s = c.getColumnIndex(OpenableColumns.SIZE);
                            if (n >= 0) name = c.getString(n);
                            if (s >= 0 && !c.isNull(s)) size = c.getLong(s);
                        }
                    }
                    HttpsURLConnection c = Net.http("/u?n=" + Uri.encode(name), "POST");
                    try {
                        c.setDoOutput(true);
                        if (size >= 0) c.setFixedLengthStreamingMode(size); else c.setChunkedStreamingMode(65536);
                        try (InputStream in = getContentResolver().openInputStream(u); OutputStream out = c.getOutputStream()) {
                            if (in == null) throw new IOException("Gagal membuka berkas");
                            byte[] buf = new byte[65536]; int n;
                            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                        }
                        if (c.getResponseCode() == 200) ok++;
                    } finally {
                        c.disconnect();
                    }
                } catch (Exception e) { Net.active = null; }
            }
            final int done = ok;
            ui.post(() -> toast(done == uris.size() ? "Terkirim: " + done + " file" : "Terkirim " + done + " dari " + uris.size() + " (cek koneksi ke PC)"));
        }).start();
    }

    private void shelf() {
        if (!Net.paired()) { toast("Pasangkan dulu dengan ZaxLet di PC"); return; }
        new Thread(() -> {
            HttpsURLConnection c = null;
            try {
                c = Net.http("/shelf.json", "GET");
                int code = c.getResponseCode();
                if (code < 200 || code >= 300) throw new IOException("HTTP " + code);
                String body;
                try (InputStream in = c.getInputStream()) { body = readAll(in); }
                org.json.JSONArray a = new org.json.JSONArray(body);
                final String[] names = new String[a.length()];
                final int[] idx = new int[a.length()];
                for (int i = 0; i < a.length(); i++) { names[i] = a.getJSONObject(i).getString("name"); idx[i] = a.getJSONObject(i).getInt("i"); }
                ui.post(() -> {
                    if (isFinishing() || isDestroyed()) return;
                    if (names.length == 0) { toast("Shelf di PC kosong"); return; }
                    new AlertDialog.Builder(this).setTitle("Shelf PC").setItems(names, (d, w) -> download(idx[w], names[w])).show();
                });
            } catch (Exception e) { Net.active = null; ui.post(() -> toast("Gagal membaca Shelf: " + e.getMessage())); }
            finally { if (c != null) c.disconnect(); }
        }).start();
    }

    private void download(int i, String name) {
        toast("Mengunduh " + name + "…");
        new Thread(() -> {
            try {
                Dl.Result r = Dl.download(this, i, name);
                ui.post(() -> toast("Tersimpan di " + r.where));
            } catch (Exception e) { Net.active = null; ui.post(() -> toast("Gagal mengunduh: " + e.getMessage())); }
        }).start();
    }

    private static String readAll(InputStream in) throws IOException {
        ByteArrayOutputStream o = new ByteArrayOutputStream();
        byte[] b = new byte[8192]; int n;
        while ((n = in.read(b)) > 0) o.write(b, 0, n);
        return o.toString("UTF-8");
    }

    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }
}
