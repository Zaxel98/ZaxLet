package com.zaxlet.hp;

import android.Manifest;
import android.app.Activity;
import android.content.pm.PackageManager;
import android.graphics.SurfaceTexture;
import android.hardware.Camera;
import android.os.Bundle;
import android.view.Gravity;
import android.view.TextureView;
import android.view.View;
import android.widget.*;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.qrcode.QRCodeReader;
import java.util.EnumMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/** Pindai QR pasangan dari ZaxLet PC: begitu terbaca, langsung dipasangkan dan layar ini menutup sendiri (tanpa tautan atau clipboard). */
public class ScanActivity extends Activity implements TextureView.SurfaceTextureListener {
    private TextureView tv;
    private Camera cam;
    private final ExecutorService worker = Executors.newSingleThreadExecutor();
    private volatile boolean busy, done;

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        Net.init(this);
        getWindow().setStatusBarColor(0xFF000000);
        getWindow().setNavigationBarColor(0xFF000000);
        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(0xFF000000);
        tv = new TextureView(this);
        tv.setSurfaceTextureListener(this);
        root.addView(tv, new FrameLayout.LayoutParams(-1, -1, Gravity.CENTER));
        TextView hint = Ui.text(this, "Arahkan ke QR di ZaxLet PC", 16, Ui.TEXT, true);
        hint.setGravity(Gravity.CENTER);
        FrameLayout.LayoutParams hp = new FrameLayout.LayoutParams(-1, -2, Gravity.TOP);
        hp.topMargin = Ui.dp(this, 48);
        root.addView(hint, hp);
        Button cancel = Ui.button(this, "Batal", false, v -> finish());
        FrameLayout.LayoutParams cp = new FrameLayout.LayoutParams(-2, -2, Gravity.BOTTOM | Gravity.CENTER_HORIZONTAL);
        cp.bottomMargin = Ui.dp(this, 40);
        root.addView(cancel, cp);
        setContentView(root);
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) requestPermissions(new String[]{Manifest.permission.CAMERA}, 5);
    }

    @Override public void onRequestPermissionsResult(int c, String[] p, int[] r) {
        if (r.length > 0 && r[0] == PackageManager.PERMISSION_GRANTED) { if (tv.isAvailable()) open(tv.getSurfaceTexture()); }
        else { Toast.makeText(this, "Izin kamera diperlukan untuk memindai", Toast.LENGTH_LONG).show(); finish(); }
    }

    @Override public void onSurfaceTextureAvailable(SurfaceTexture st, int w, int h) {
        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) open(st);
    }
    @Override public boolean onSurfaceTextureDestroyed(SurfaceTexture st) { release(); return true; }
    @Override public void onSurfaceTextureSizeChanged(SurfaceTexture st, int w, int h) { }
    @Override public void onSurfaceTextureUpdated(SurfaceTexture st) { }

    private void open(SurfaceTexture st) {
        if (cam != null) return;
        try {
            cam = Camera.open();   // kamera belakang; gagal bila kamera sedang dipakai kirim video ke PC
            Camera.Parameters p = cam.getParameters();
            Camera.Size best = null;   // pratinjau 1280x720 atau terdekat: cukup tajam untuk QR, ringan untuk diurai
            for (Camera.Size s : p.getSupportedPreviewSizes())
                if (best == null || Math.abs(s.width * s.height - 1280 * 720) < Math.abs(best.width * best.height - 1280 * 720)) best = s;
            p.setPreviewSize(best.width, best.height);
            if (p.getSupportedFocusModes().contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) p.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
            cam.setParameters(p);
            cam.setDisplayOrientation(90);
            // pratinjau potret: lebar tampilan = sisi pendek sensor
            int vw = getResources().getDisplayMetrics().widthPixels;
            tv.setLayoutParams(new FrameLayout.LayoutParams(vw, (int) ((long) vw * best.width / best.height), Gravity.CENTER));
            cam.setPreviewTexture(st);
            final int w = best.width, h = best.height;
            cam.setPreviewCallback((data, c) -> decode(data, w, h));
            cam.startPreview();
        } catch (Exception e) {
            Toast.makeText(this, "Kamera tidak bisa dibuka (sedang dipakai?)", Toast.LENGTH_LONG).show();
            finish();
        }
    }

    private void decode(final byte[] data, final int w, final int h) {
        if (busy || done) return;
        busy = true;
        final byte[] copy = data.clone();   // buffer kamera dipakai ulang
        worker.execute(() -> {
            try {
                Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
                hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
                String text = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(new PlanarYUVLuminanceSource(copy, w, h, 0, 0, w, h, false))), hints).getText();
                if (text.startsWith("zaxlet://pair") && !done) {
                    done = true;
                    runOnUiThread(() -> found(text));
                }
            } catch (Exception e) { /* tidak ada QR di bingkai ini */ }
            busy = false;
        });
    }

    private void found(String link) {
        if (Net.savePairing(link)) {
            Toast.makeText(this, ConnService.alive ? "Terpasang, menyambung ke PC…" : "Terpasang. Ketuk \"Aktifkan\".", Toast.LENGTH_LONG).show();
            if (ConnService.alive) ConnService.kickNow();
        } else Toast.makeText(this, "QR tidak valid", Toast.LENGTH_SHORT).show();
        finish();
    }

    private void release() {
        if (cam == null) return;
        try { cam.setPreviewCallback(null); cam.stopPreview(); } catch (Exception e) { }
        cam.release(); cam = null;
    }

    @Override protected void onDestroy() { release(); worker.shutdownNow(); super.onDestroy(); }
}
