package com.zaxlet.hp;

import android.content.ContentValues;
import android.content.Context;
import android.net.Uri;
import android.os.Build;
import android.os.Environment;
import android.provider.MediaStore;
import java.io.File;
import java.io.FileOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import javax.net.ssl.HttpsURLConnection;

/** File downloader from PC Shelf (/d/<idx>) to Download/ZaxLet, used by MainActivity and ConnService. */
final class Dl {
    static final class Result {
        final Uri uri;
        final String where;
        final String name;
        Result(Uri uri, String where, String name) { this.uri = uri; this.where = where; this.name = name; }
    }

    static String sanitizeName(String raw) {
        if (raw == null) return "file";
        StringBuilder sb = new StringBuilder(raw.length());
        for (int i = 0; i < raw.length(); i++) {
            char ch = raw.charAt(i);
            if (ch == '/' || ch == '\\') sb.append('_');
            else if (ch >= 32 && ch != 127) sb.append(ch);
        }
        String s = sb.toString().trim();
        while (s.contains("..")) s = s.replace("..", "_");
        if (s.isEmpty() || s.equals(".")) return "file";
        return s;
    }

    static Result download(Context ctx, int idx, String rawName) throws Exception {
        String name = sanitizeName(rawName);
        HttpsURLConnection c = Net.http("/d/" + idx, "GET");
        try {
            int code = c.getResponseCode();
            if (code < 200 || code >= 300) throw new IOException("HTTP " + code);
            Uri uri = null;
            File legacyFile = null;
            String where;
            OutputStream out = null;
            boolean ok = false;
            try {
                if (Build.VERSION.SDK_INT >= 29) {
                    ContentValues v = new ContentValues();
                    v.put(MediaStore.Downloads.DISPLAY_NAME, name);
                    v.put(MediaStore.Downloads.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/ZaxLet");
                    uri = ctx.getContentResolver().insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, v);
                    if (uri == null) throw new IOException("Gagal membuat berkas di MediaStore");
                    out = ctx.getContentResolver().openOutputStream(uri);
                    if (out == null) throw new IOException("Gagal membuka berkas tujuan");
                    where = "Download/ZaxLet";
                } else {
                    File dir = ctx.getExternalFilesDir(Environment.DIRECTORY_DOWNLOADS);
                    if (dir != null) dir.mkdirs();
                    legacyFile = new File(dir, name);
                    out = new FileOutputStream(legacyFile);
                    uri = Uri.fromFile(legacyFile);
                    where = legacyFile.getParent();
                }
                try (InputStream in = c.getInputStream()) {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
                }
                ok = true;
                return new Result(uri, where, name);
            } finally {
                if (out != null) try { out.close(); } catch (Exception ignored) { }
                if (!ok) {
                    if (Build.VERSION.SDK_INT >= 29 && uri != null) {
                        try { ctx.getContentResolver().delete(uri, null, null); } catch (Exception ignored) { }
                    } else if (legacyFile != null) {
                        legacyFile.delete();
                    }
                }
            }
        } finally {
            c.disconnect();
        }
    }
}
