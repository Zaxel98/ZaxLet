package com.zaxlet.hp;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.hardware.display.DisplayManager;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.media.projection.MediaProjection;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.util.Log;
import android.view.Display;
import android.view.WindowManager;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import org.json.JSONObject;

/**
 * Menangkap layar lewat MediaProjection yang sudah ada, mengompres JPEG adaptif (maks 12 fps, hanya bingkai terbaru),
 * mengirim ke PC (/scr/ws), dan menerima perintah kontrol dari PC untuk diteruskan ke ZaxAccess.
 */
final class ScreenStream {
    private static final long MIN_FRAME_MS = 83;   // <= 12 fps
    private static final long SLOW_SEND_MS = 250;
    private static final long STEP_UP_MS = 15000;
    private static final int[] QUALITIES = {45, 40, 35, 30};
    private static final int[] LONG_SIDES = {960, 720, 720, 540};

    private final Context ctx;
    private final MediaProjection mp;
    private final Runnable onStopped;

    private volatile boolean stopping;
    private volatile int level;
    private int curW, curH, curRot = -1;
    private volatile int capN;   // bingkai yang dikompres, untuk log diagnostik
    private int sentN; private long sentBytes, maxSendMs, lastLogMs;
    private String infoJson;
    private long lastCaptureMs;

    private HandlerThread capThread;
    private Handler capHandler;
    private DisplayManager dm;
    private DisplayManager.DisplayListener dispListener;
    private ImageReader reader;
    private VirtualDisplay vdisplay;
    private Bitmap reusableBmp;

    private Thread sendThread;
    private volatile Ws ws;
    private final Object frameLock = new Object();
    private final Object wake = new Object();
    private byte[] latestFrame;
    private boolean infoDirty;

    ScreenStream(Context ctx, MediaProjection mp, Runnable onStopped) {
        this.ctx = ctx.getApplicationContext();
        this.mp = mp;
        this.onStopped = onStopped;
    }

    synchronized void start() {
        if (stopping || capThread != null) return;
        capThread = new HandlerThread("zaxlet-scr-cap");
        capThread.start();
        capHandler = new Handler(capThread.getLooper());
        dm = (DisplayManager) ctx.getSystemService(Context.DISPLAY_SERVICE);
        dispListener = new DisplayManager.DisplayListener() {
            @Override public void onDisplayAdded(int id) { }
            @Override public void onDisplayRemoved(int id) { }
            @Override public void onDisplayChanged(int id) {
                if (id == Display.DEFAULT_DISPLAY && !stopping) setupCapture(false);
            }
        };
        if (dm != null) dm.registerDisplayListener(dispListener, capHandler);
        capHandler.post(() -> setupCapture(true));

        sendThread = new Thread(this::sendLoop, "zaxlet-scr-tx");
        sendThread.start();
    }

    private void setupCapture(boolean force) {
        if (stopping) return;
        WindowManager wm = (WindowManager) ctx.getSystemService(Context.WINDOW_SERVICE);
        if (wm == null) return;
        Display disp = wm.getDefaultDisplay();
        if (disp == null) return;

        int rawW, rawH;
        if (Build.VERSION.SDK_INT >= 30) {
            Rect b = wm.getCurrentWindowMetrics().getBounds();
            rawW = b.width();
            rawH = b.height();
        } else {
            DisplayMetrics real = new DisplayMetrics();
            disp.getRealMetrics(real);
            rawW = real.widthPixels;
            rawH = real.heightPixels;
        }
        int rot = disp.getRotation();
        int maxSide = LONG_SIDES[level];
        int longSide = Math.max(rawW, rawH);
        float scale = longSide > maxSide ? (float) maxSide / longSide : 1f;
        int w = Math.max(2, ((int) (rawW * scale)) & ~1);
        int h = Math.max(2, ((int) (rawH * scale)) & ~1);

        if (!force && w == curW && h == curH && rot == curRot && vdisplay != null) return;

        if (stopping) return;

        int dpi = ctx.getResources().getDisplayMetrics().densityDpi;
        try {
            ImageReader nr = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2);
            nr.setOnImageAvailableListener(this::onImage, capHandler);
            if (vdisplay == null) {
                vdisplay = mp.createVirtualDisplay("zaxlet-scr", w, h, dpi, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, nr.getSurface(), null, capHandler);
            } else {
                // Android 14+: satu MediaProjection hanya boleh membuat satu VirtualDisplay, jadi ukuran diubah di tempat
                vdisplay.resize(w, h, dpi);
                vdisplay.setSurface(nr.getSurface());
            }
            ImageReader old = reader;
            reader = nr;
            if (old != null) try { old.close(); } catch (Exception ignored) { }
            if (reusableBmp != null) { reusableBmp.recycle(); reusableBmp = null; }
        } catch (Throwable e) {
            // jangan biarkan galat di thread tangkap mematikan aplikasi; video berhenti, audio lanjut
            new Thread(this::stop, "zaxlet-scr-stop").start();
            return;
        }
        curW = w;
        curH = h;
        curRot = rot;

        try {
            JSONObject info = new JSONObject();
            info.put("t", "info");
            info.put("w", w);
            info.put("h", h);
            info.put("rot", rot);
            synchronized (frameLock) {
                infoJson = info.toString();
                infoDirty = true;
                frameLock.notifyAll();
            }
        } catch (Exception ignored) { }
    }

    private void onImage(ImageReader r) {
        if (stopping) return;
        Image img = null;
        try {
            img = r.acquireLatestImage();
            if (img == null) return;
            long now = SystemClock.elapsedRealtime();
            if (now - lastCaptureMs < MIN_FRAME_MS) return;
            lastCaptureMs = now;

            Image.Plane plane = img.getPlanes()[0];
            ByteBuffer buf = plane.getBuffer();
            int pixelStride = plane.getPixelStride();
            int rowStride = plane.getRowStride();
            int w = img.getWidth(), h = img.getHeight();
            int bmpW = w + (rowStride - pixelStride * w) / pixelStride;
            if (reusableBmp == null || reusableBmp.getWidth() != bmpW || reusableBmp.getHeight() != h) {
                if (reusableBmp != null) reusableBmp.recycle();
                reusableBmp = Bitmap.createBitmap(bmpW, h, Bitmap.Config.ARGB_8888);
            }
            reusableBmp.copyPixelsFromBuffer(buf);

            Bitmap src = reusableBmp;
            Bitmap cropped = null;
            if (bmpW != w) {
                cropped = Bitmap.createBitmap(reusableBmp, 0, 0, w, h);
                src = cropped;
            }
            ByteArrayOutputStream baos = new ByteArrayOutputStream(65536);
            src.compress(Bitmap.CompressFormat.JPEG, QUALITIES[level], baos);
            if (cropped != null) cropped.recycle();

            byte[] jpeg = baos.toByteArray();
            synchronized (frameLock) {
                capN++;
                latestFrame = jpeg;
                frameLock.notifyAll();
            }
        } catch (Throwable ignored) {
        } finally {
            if (img != null) img.close();
        }
    }

    private void sendLoop() {
        int backoff = 1000;
        long fastSinceMs = 0;
        while (!stopping) {
            Ws w = null;
            Thread rxThread = null;
            try {
                w = new Ws("/scr/ws");
                if (stopping) { w.close(); break; }
                w.sock.setTcpNoDelay(true);
                w.sock.setSendBufferSize(131072);
                backoff = 1000;
                ws = w;

                final Ws activeWs = w;
                rxThread = new Thread(() -> readLoop(activeWs), "zaxlet-scr-rx");
                rxThread.start();

                String initInfo;
                synchronized (frameLock) {
                    initInfo = infoJson;
                    infoDirty = false;
                }
                if (initInfo != null) w.sendText(initInfo);

                while (!stopping && ws == w) {
                    byte[] frame = null;
                    String pendingInfo = null;
                    boolean keepAlive = false;
                    synchronized (frameLock) {
                        long waitFrom = SystemClock.elapsedRealtime();
                        while (!stopping && ws == w && latestFrame == null && !infoDirty) {
                            // layar diam = tak ada bingkai baru; PC menutup soket yang diam >75 dtk, jadi kirim detak tiap 15 dtk
                            if (SystemClock.elapsedRealtime() - waitFrom >= 15000) { keepAlive = true; break; }
                            try { frameLock.wait(15000); } catch (InterruptedException e) { return; }
                        }
                        if (stopping || ws != w) break;
                        if (infoDirty) {
                            pendingInfo = infoJson;
                            infoDirty = false;
                        }
                        frame = latestFrame;
                        latestFrame = null;
                    }
                    if (keepAlive) { w.sendText("{\"t\":\"ka\"}"); continue; }
                    if (pendingInfo != null) w.sendText(pendingInfo);
                    if (frame == null) continue;
                    if (AudioService.audioQueued >= 5) {
                        // Antrean audio penuh: tunda video (hanya simpan bingkai terbaru) agar audio tidak kalah di Wi-Fi
                        synchronized (wake) {
                            if (!stopping && ws == w) {
                                try { wake.wait(25); } catch (InterruptedException e) { return; }
                            }
                        }
                        continue;
                    }

                    long t0 = SystemClock.elapsedRealtime();
                    w.sendBinary(frame, frame.length);
                    long dt = SystemClock.elapsedRealtime() - t0;
                    sentN++; sentBytes += frame.length; if (dt > maxSendMs) maxSendMs = dt;
                    long nowLog = SystemClock.elapsedRealtime();
                    if (nowLog - lastLogMs >= 5000) {
                        Log.i("ZaxLetScr", "cap=" + capN + " sent=" + sentN + " avgKB=" + (sentN > 0 ? sentBytes / sentN / 1024 : 0) + " maxSendMs=" + maxSendMs + " level=" + level + " size=" + curW + "x" + curH + " audQ=" + AudioService.audioQueued);
                        lastLogMs = nowLog; sentN = 0; sentBytes = 0; maxSendMs = 0; capN = 0;
                    }

                    long now = SystemClock.elapsedRealtime();
                    if (dt > SLOW_SEND_MS) {
                        fastSinceMs = 0;
                        if (level < QUALITIES.length - 1) {
                            int prevSide = LONG_SIDES[level];
                            level++;
                            if (LONG_SIDES[level] != prevSide && capHandler != null) capHandler.post(() -> setupCapture(true));
                        }
                    } else {
                        if (fastSinceMs == 0) fastSinceMs = now;
                        else if (now - fastSinceMs >= STEP_UP_MS && level > 0) {
                            int prevSide = LONG_SIDES[level];
                            level--;
                            fastSinceMs = now;
                            if (LONG_SIDES[level] != prevSide && capHandler != null) capHandler.post(() -> setupCapture(true));
                        }
                    }

                    if (dt > 0 && !stopping && ws == w) {
                        synchronized (wake) {
                            if (!stopping && ws == w) {
                                try { wake.wait(dt); } catch (InterruptedException e) { return; }
                            }
                        }
                    }
                }
            } catch (Throwable ignored) {
            } finally {
                if (ws == w) ws = null;
                if (w != null) w.close();
                if (rxThread != null) {
                    rxThread.interrupt();
                    try { rxThread.join(500); } catch (InterruptedException ignored) { }
                }
            }
            if (stopping) break;
            synchronized (wake) {
                if (!stopping) {
                    try { wake.wait(backoff); } catch (InterruptedException e) { break; }
                }
            }
            backoff = Math.min(backoff * 2, 10000);
        }
    }

    private void readLoop(Ws w) {
        try {
            String msg;
            while (!stopping && ws == w && (msg = w.readText()) != null) {
                if (msg.isEmpty()) continue;
                try {
                    JSONObject o = new JSONObject(msg);
                    ZaxAccess acc = ZaxAccess.instance;
                    if (acc == null) {
                        JSONObject err = new JSONObject();
                        err.put("t", "err");
                        err.put("m", "Layanan aksesibilitas belum aktif");
                        w.sendText(err.toString());
                    } else {
                        acc.perform(o);
                    }
                } catch (Exception ignored) { }
            }
            // PC menutup soket video dengan bersih: hentikan video, biarkan audio tetap berjalan
            if (!stopping && ws == w) {
                new Thread(this::stop, "zaxlet-scr-stop").start();
            }
        } catch (Throwable ignored) {
            // Putus jaringan: sendLoop akan menyambung ulang jika !stopping
            synchronized (frameLock) {
                if (ws == w) { ws = null; frameLock.notifyAll(); }
            }
            synchronized (wake) { wake.notifyAll(); }
        }
    }

    private void releaseCapture() {
        try { if (vdisplay != null) { vdisplay.release(); vdisplay = null; } } catch (Exception ignored) { }
        try { if (reader != null) { reader.close(); reader = null; } } catch (Exception ignored) { }
        try { if (reusableBmp != null) { reusableBmp.recycle(); reusableBmp = null; } } catch (Exception ignored) { }
    }

    void stop() {
        synchronized (this) {
            if (stopping) return;
            stopping = true;
        }
        synchronized (frameLock) { latestFrame = null; frameLock.notifyAll(); }
        synchronized (wake) { wake.notifyAll(); }
        Ws w = ws;
        ws = null;
        if (w != null) w.close();
        if (dm != null && dispListener != null) {
            try { dm.unregisterDisplayListener(dispListener); } catch (Exception ignored) { }
        }
        if (capHandler != null) {
            capHandler.post(this::releaseCapture);
        } else {
            releaseCapture();
        }
        if (capThread != null) {
            capThread.quitSafely();
            try { capThread.join(1000); } catch (InterruptedException ignored) { }
            capThread = null;
            capHandler = null;
        }
        if (sendThread != null && Thread.currentThread() != sendThread) {
            sendThread.interrupt();
            try { sendThread.join(1000); } catch (InterruptedException ignored) { }
            sendThread = null;
        }
        if (onStopped != null) onStopped.run();
    }
}
