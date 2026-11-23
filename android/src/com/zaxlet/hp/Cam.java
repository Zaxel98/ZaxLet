package com.zaxlet.hp;

import android.content.Context;
import android.graphics.ImageFormat;
import android.graphics.Rect;
import android.graphics.YuvImage;
import android.hardware.camera2.*;
import android.media.Image;
import android.media.ImageReader;
import android.os.Handler;
import android.os.HandlerThread;
import android.util.Size;
import java.io.ByteArrayOutputStream;
import java.nio.ByteBuffer;
import java.util.Collections;

/** Camera2 -> upright JPEG -> WebSocket /cam/ws. Used by ConnService while the PC requests the camera. */
final class Cam {
    private final Context ctx;
    private HandlerThread th;
    private Handler h;
    private CameraDevice dev;
    private CameraCaptureSession sess;
    private ImageReader reader;
    private Ws ws;
    private volatile boolean running;
    private long last;
    private int minGap, sensor;
    private boolean front;
    private volatile int devOrient;   // 0/90/180/270 from phone orientation sensor (still works even when screen rotation is locked)
    private android.view.OrientationEventListener orient;
    private boolean sending;
    private final ByteArrayOutputStream jpg = new ByteArrayOutputStream(200000);
    String error = "";

    Cam(Context c) { ctx = c; }

    boolean running() { return running; }

    synchronized void start(int height, int fps, boolean front) {
        stop();
        running = true;
        sending = false;
        minGap = 1000 / Math.max(1, fps);
        th = new HandlerThread("cam");
        th.start();
        h = new Handler(th.getLooper());
        final HandlerThread myTh = th;
        h.post(() -> {
            try { open(height, front, myTh); }
            catch (Throwable e) { synchronized (Cam.this) { if (running && th == myTh) { error = e.toString(); stop(); } } }
        });
    }

    @SuppressWarnings("MissingPermission")
    private void open(int height, boolean front, final HandlerThread myTh) throws Exception {
        CameraManager cm = (CameraManager) ctx.getSystemService(Context.CAMERA_SERVICE);
        String id = null;
        CameraCharacteristics cc = null;
        for (String s : cm.getCameraIdList()) {
            CameraCharacteristics c = cm.getCameraCharacteristics(s);
            Integer f = c.get(CameraCharacteristics.LENS_FACING);
            if (f != null && (f == CameraCharacteristics.LENS_FACING_FRONT) == front) { id = s; cc = c; break; }
        }
        if (id == null) throw new Exception("kamera tidak ada");
        int sensorOrient = cc.get(CameraCharacteristics.SENSOR_ORIENTATION);
        Size best = null;
        for (Size s : cc.get(CameraCharacteristics.SCALER_STREAM_CONFIGURATION_MAP).getOutputSizes(ImageFormat.YUV_420_888)) {
            if (s.getWidth() < s.getHeight()) continue;
            if (best == null || Math.abs(s.getHeight() - height) < Math.abs(best.getHeight() - height)) best = s;
        }
        final Size size = best;
        // connect WS outside the lock so stop() is not blocked by network I/O
        Ws newWs = new Ws("/cam/ws");
        synchronized (this) {
            if (!running || th != myTh) { newWs.close(); return; }
            sensor = sensorOrient;
            this.front = front;
            orient = new android.view.OrientationEventListener(ctx) {
                public void onOrientationChanged(int o) { if (o != ORIENTATION_UNKNOWN) devOrient = ((o + 45) / 90 * 90) % 360; }
            };
            if (orient.canDetectOrientation()) orient.enable();
            ws = newWs;
            reader = ImageReader.newInstance(size.getWidth(), size.getHeight(), ImageFormat.YUV_420_888, 2);
            reader.setOnImageAvailableListener(r -> onFrame(r, size), h);
            cm.openCamera(id, new CameraDevice.StateCallback() {
                public void onOpened(CameraDevice d) {
                    synchronized (Cam.this) {
                        if (!running || th != myTh || reader == null) {
                            try { d.close(); } catch (Exception ignored) { }
                            return;
                        }
                        dev = d;
                        try {
                            CaptureRequest.Builder b = d.createCaptureRequest(CameraDevice.TEMPLATE_RECORD);
                            b.addTarget(reader.getSurface());
                            d.createCaptureSession(Collections.singletonList(reader.getSurface()), new SessCb(b, myTh), h);
                        } catch (Exception e) { error = e.toString(); stop(); }
                    }
                }
                public void onDisconnected(CameraDevice d) {
                    synchronized (Cam.this) {
                        if (th == myTh) {
                            if (dev == null) try { d.close(); } catch (Exception ignored) { }
                            stop();
                        } else {
                            try { d.close(); } catch (Exception ignored) { }
                        }
                    }
                }
                public void onError(CameraDevice d, int e) {
                    synchronized (Cam.this) {
                        if (th == myTh) {
                            if (dev == null) try { d.close(); } catch (Exception ignored) { }
                            error = "kamera error " + e;
                            stop();
                        } else {
                            try { d.close(); } catch (Exception ignored) { }
                        }
                    }
                }
            }, h);
        }
    }

    // named class (not anonymous inside anonymous): older build-tools d8 fails on nested EnclosingMethod under newer JDK
    private final class SessCb extends CameraCaptureSession.StateCallback {
        private final CaptureRequest.Builder b;
        private final HandlerThread myTh;
        SessCb(CaptureRequest.Builder b, HandlerThread myTh) { this.b = b; this.myTh = myTh; }
        @Override public void onConfigured(CameraCaptureSession s) {
            synchronized (Cam.this) {
                if (!running || th != myTh || dev == null) {
                    try { s.close(); } catch (Exception ignored) { }
                    return;
                }
                sess = s;
                try { s.setRepeatingRequest(b.build(), null, h); } catch (Exception e) { error = e.toString(); stop(); }
            }
        }
        @Override public void onConfigureFailed(CameraCaptureSession s) {
            synchronized (Cam.this) {
                try { s.close(); } catch (Exception ignored) { }
                if (running && th == myTh) { error = "konfigurasi gagal"; stop(); }
            }
        }
    }

    private void onFrame(ImageReader r, Size size) {
        byte[] data;
        Ws w;
        try {
            synchronized (this) {
                if (!running || reader != r || ws == null || sending) return;
                Image img = r.acquireLatestImage();
                if (img == null) return;
                try {
                    long now = System.currentTimeMillis();
                    if (now - last < minGap) return;
                    last = now;
                    int w0 = img.getWidth(), hh = img.getHeight();
                    byte[] nv21 = toNv21(img, w0, hh);
                    int ow = w0, oh = hh;
                    // rotate clockwise to upright: official Android formula for JPEG (front and back differ in direction)
                    int rot = front ? (sensor - devOrient + 360) % 360 : (sensor + devOrient) % 360;
                    if (rot == 90 || rot == 270) { nv21 = rotate(nv21, w0, hh, rot == 90); ow = hh; oh = w0; }
                    else if (rot == 180) nv21 = rotate180(nv21, w0, hh);
                    jpg.reset();
                    new YuvImage(nv21, ImageFormat.NV21, ow, oh, null).compressToJpeg(new Rect(0, 0, ow, oh), 70, jpg);
                    data = jpg.toByteArray();
                    w = ws;
                    sending = true;
                } finally { img.close(); }
            }
            try { w.sendBinary(data, data.length); }
            finally { synchronized (this) { sending = false; } }
        } catch (Exception e) {
            synchronized (this) { if (running) { error = e.toString(); stop(); } }
        }
    }

    private static byte[] toNv21(Image img, int w, int h) {
        byte[] out = new byte[w * h * 3 / 2];
        Image.Plane[] p = img.getPlanes();
        ByteBuffer y = p[0].getBuffer();
        int rs = p[0].getRowStride(), pos = 0;
        for (int r = 0; r < h; r++) { y.position(r * rs); y.get(out, pos, w); pos += w; }
        ByteBuffer u = p[1].getBuffer(), v = p[2].getBuffer();
        int rsU = p[1].getRowStride(), ps = p[1].getPixelStride();
        for (int r = 0; r < h / 2; r++)
            for (int c = 0; c < w / 2; c++) {
                int i = r * rsU + c * ps;
                out[pos++] = v.get(i);
                out[pos++] = u.get(i);
            }
        return out;
    }

    private static byte[] rotate180(byte[] s, int w, int h) {
        byte[] d = new byte[s.length];
        int n = w * h;
        for (int i = 0; i < n; i++) d[i] = s[n - 1 - i];
        for (int i = n; i < s.length; i += 2) { int j = s.length - 2 - (i - n); d[i] = s[j]; d[i + 1] = s[j + 1]; }
        return d;
    }

    /** Rotate NV21 90 degrees clockwise (cw) or 270 (ccw) so it is upright for portrait phone. */
    private static byte[] rotate(byte[] s, int w, int h, boolean cw) {
        byte[] d = new byte[s.length];
        int k = 0;
        if (cw) { for (int x = 0; x < w; x++) for (int y = h - 1; y >= 0; y--) d[k++] = s[y * w + x]; }
        else { for (int x = w - 1; x >= 0; x--) for (int y = 0; y < h; y++) d[k++] = s[y * w + x]; }
        int base = w * h;
        k = base;
        if (cw) {
            for (int x = 0; x < w; x += 2) for (int y = h / 2 - 1; y >= 0; y--) { int i = base + y * w + x; d[k++] = s[i]; d[k++] = s[i + 1]; }
        } else {
            for (int x = w - 2; x >= 0; x -= 2) for (int y = 0; y < h / 2; y++) { int i = base + y * w + x; d[k++] = s[i]; d[k++] = s[i + 1]; }
        }
        return d;
    }

    synchronized void stop() {
        running = false;
        if (orient != null) { orient.disable(); orient = null; }
        try { if (sess != null) sess.close(); } catch (Exception e) { }
        try { if (dev != null) dev.close(); } catch (Exception e) { }
        try { if (reader != null) reader.close(); } catch (Exception e) { }
        if (ws != null) ws.close();
        sess = null; dev = null; reader = null; ws = null;
        if (th != null) { th.quitSafely(); th = null; }
        h = null;
    }
}
