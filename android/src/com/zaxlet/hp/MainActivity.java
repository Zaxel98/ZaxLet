package com.zaxlet.hp;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.ClipData;
import android.content.ContentValues;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.database.Cursor;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
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

/** Layar tunggal: pasangkan, nyalakan layanan latar, kirim file ke PC, ambil file dari Shelf. Juga penerima menu Bagikan. */
public class MainActivity extends Activity {
    private TextView status;
    private Button toggle;
    private EditText link;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final Runnable tick = new Runnable() { public void run() { refresh(); ui.postDelayed(this, 1500); } };

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        Net.init(this);
        getWindow().setStatusBarColor(Color.parseColor("#0B0B0D"));
        getWindow().setNavigationBarColor(Color.parseColor("#0B0B0D"));
        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(Color.parseColor("#0B0B0D"));
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(36), dp(20), dp(24));
        sv.addView(root);

        TextView t = text("ZaxLet HP", 26, Color.WHITE, true);
        root.addView(t);
        status = text("", 14, Color.parseColor("#9A9AA2"), false);
        status.setPadding(0, dp(6), 0, dp(14));
        root.addView(status);

        link = new EditText(this);
        link.setHint("Tempel tautan pasangan (zaxlet://pair?…) atau scan QR dari ZaxLet");
        link.setTextColor(Color.WHITE); link.setHintTextColor(Color.parseColor("#6A6A72")); link.setTextSize(13);
        link.setBackground(bg("#1C1C20", 12));
        link.setPadding(dp(12), dp(10), dp(12), dp(10));
        root.addView(link, lp());
        root.addView(btn("Pasangkan", "#1C1C20", Color.WHITE, v -> pair(link.getText().toString())), lp());
        toggle = btn("", "#2DD4BF", Color.parseColor("#04201C"), v -> {
            if (ConnService.alive) startService(new Intent(this, ConnService.class).setAction("STOP")); else startService();
            ui.postDelayed(this::refresh, 400);
        });
        root.addView(toggle, lp());
        root.addView(btn("Kirim file ke PC", "#1C1C20", Color.WHITE, v -> pickFiles()), lp());
        root.addView(btn("Ambil file dari Shelf PC", "#1C1C20", Color.WHITE, v -> shelf()), lp());
        root.addView(btn("Setelan", "#1C1C20", Color.WHITE, v -> startActivity(new Intent(this, SettingsActivity.class))), lp());
        TextView hint = text("Layanan latar belakang menjaga sambungan ke PC walau layar mati (notifikasi dan balasan chat). "
                + "Setelah HP restart, buka aplikasi ini sekali lalu aktifkan lagi.", 12, Color.parseColor("#6A6A72"), false);
        hint.setPadding(0, dp(12), 0, 0);
        root.addView(hint);
        setContentView(sv);
        handle(getIntent());
    }

    @Override protected void onNewIntent(Intent i) { super.onNewIntent(i); handle(i); }
    @Override protected void onResume() { super.onResume(); ui.post(tick); }
    @Override protected void onPause() { super.onPause(); ui.removeCallbacks(tick); }

    private void refresh() {
        toggle.setText(ConnService.alive ? "Matikan layanan latar belakang" : "Aktifkan layanan latar belakang");
        String s = Net.paired() ? "Terpasang dengan PC (" + (Net.get("lan").isEmpty() ? "" : "LAN " + Net.get("lan")) + (Net.get("ts").isEmpty() ? "" : "  Tailscale " + Net.get("ts")) + ")" : "Belum dipasangkan";
        String listeners = Settings.Secure.getString(getContentResolver(), "enabled_notification_listeners");
        boolean notif = listeners != null && listeners.contains(getPackageName());
        status.setText(s + "\nLayanan: " + (ConnService.alive ? ConnService.status : "mati") + "\nAkses notifikasi: " + (notif ? "aktif" : "belum diizinkan"));
    }

    private void handle(Intent i) {
        if (i == null) return;
        String a = i.getAction();
        if (Intent.ACTION_VIEW.equals(a) && i.getData() != null) pair(i.getData().toString());
        else if (Intent.ACTION_SEND.equals(a)) {
            Uri u = i.getParcelableExtra(Intent.EXTRA_STREAM);
            if (u != null) { ArrayList<Uri> l = new ArrayList<>(); l.add(u); upload(l); }
        } else if (Intent.ACTION_SEND_MULTIPLE.equals(a)) {
            ArrayList<Uri> l = i.getParcelableArrayListExtra(Intent.EXTRA_STREAM);
            if (l != null) upload(l);
        }
    }

    private void pair(String s) {
        if (Net.savePairing(s)) { toast("Terpasang. Ketuk \"Aktifkan layanan\"."); link.setText(""); } else toast("Tautan tidak valid");
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

    @Override public void onRequestPermissionsResult(int c, String[] p, int[] r) { if (c == 7) startService(); }

    private void batteryOpt() {
        try { startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))); }
        catch (Exception e) { startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); }
    }

    private void pickFiles() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT).addCategory(Intent.CATEGORY_OPENABLE).setType("*/*").putExtra(Intent.EXTRA_ALLOW_MULTIPLE, true);
        startActivityForResult(i, 9);
    }

    @Override protected void onActivityResult(int rc, int res, Intent d) {
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
                    c.setDoOutput(true);
                    if (size >= 0) c.setFixedLengthStreamingMode(size); else c.setChunkedStreamingMode(65536);
                    try (InputStream in = getContentResolver().openInputStream(u); OutputStream out = c.getOutputStream()) {
                        byte[] buf = new byte[65536]; int n;
                        while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                    }
                    if (c.getResponseCode() == 200) ok++;
                    c.disconnect();
                } catch (Exception e) { Net.active = null; }
            }
            final int done = ok;
            ui.post(() -> toast(done == uris.size() ? "Terkirim: " + done + " file" : "Terkirim " + done + " dari " + uris.size() + " (cek koneksi ke PC)"));
        }).start();
    }

    private void shelf() {
        if (!Net.paired()) { toast("Pasangkan dulu dengan ZaxLet di PC"); return; }
        new Thread(() -> {
            try {
                HttpsURLConnection c = Net.http("/shelf.json", "GET");
                String body = readAll(c.getInputStream());
                org.json.JSONArray a = new org.json.JSONArray(body);
                final String[] names = new String[a.length()];
                final int[] idx = new int[a.length()];
                for (int i = 0; i < a.length(); i++) { names[i] = a.getJSONObject(i).getString("name"); idx[i] = a.getJSONObject(i).getInt("i"); }
                ui.post(() -> {
                    if (names.length == 0) { toast("Shelf di PC kosong"); return; }
                    new AlertDialog.Builder(this).setTitle("Shelf PC").setItems(names, (d, w) -> download(idx[w], names[w])).show();
                });
            } catch (Exception e) { Net.active = null; ui.post(() -> toast("Gagal membaca Shelf: " + e.getMessage())); }
        }).start();
    }

    private void download(int i, String name) {
        toast("Mengunduh " + name + "…");
        new Thread(() -> {
            try {
                HttpsURLConnection c = Net.http("/d/" + i, "GET");
                OutputStream out; String where;
                if (Build.VERSION.SDK_INT >= 29) {
                    ContentValues v = new ContentValues();
                    v.put(MediaStore.Downloads.DISPLAY_NAME, name);
                    v.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/ZaxLet");
                    Uri u = getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v);
                    out = getContentResolver().openOutputStream(u);
                    where = "Download/ZaxLet";
                } else {
                    File f = new File(getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS), name);
                    out = new FileOutputStream(f);
                    where = f.getParent();
                }
                try (InputStream in = c.getInputStream(); OutputStream o = out) {
                    byte[] buf = new byte[65536]; int n;
                    while ((n = in.read(buf)) > 0) o.write(buf, 0, n);
                }
                final String w = where;
                ui.post(() -> toast("Tersimpan di " + w));
            } catch (Exception e) { Net.active = null; ui.post(() -> toast("Gagal mengunduh: " + e.getMessage())); }
        }).start();
    }

    private static String readAll(InputStream in) throws IOException {
        ByteArrayOutputStream o = new ByteArrayOutputStream();
        byte[] b = new byte[8192]; int n;
        while ((n = in.read(b)) > 0) o.write(b, 0, n);
        return o.toString("UTF-8");
    }

    // ---------- UI kecil ----------
    private int dp(int v) { return (int) (v * getResources().getDisplayMetrics().density); }
    private void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_SHORT).show(); }
    private GradientDrawable bg(String c, int r) { GradientDrawable g = new GradientDrawable(); g.setColor(Color.parseColor(c)); g.setCornerRadius(dp(r)); return g; }
    private LinearLayout.LayoutParams lp() { LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2); p.topMargin = dp(10); return p; }
    private TextView text(String s, int sp, int color, boolean bold) {
        TextView t = new TextView(this); t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(null, android.graphics.Typeface.BOLD);
        return t;
    }
    private Button btn(String s, String bgc, int fg, View.OnClickListener l) {
        Button b = new Button(this); b.setText(s); b.setAllCaps(false); b.setTextColor(fg); b.setTextSize(15);
        b.setBackground(bg(bgc, 14)); b.setPadding(dp(14), dp(14), dp(14), dp(14)); b.setOnClickListener(l); b.setGravity(Gravity.CENTER);
        return b;
    }
}
