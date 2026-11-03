package com.zaxlet.hp;

import android.content.Context;
import android.content.SharedPreferences;
import java.io.*;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.*;

/** Pengaturan pasangan + koneksi TLS ke PC. Sertifikat PC dipin lewat sidik jari SHA-256 dari tautan pasangan. */
final class Net {
    static SharedPreferences sp;
    static String active;   // host yang terakhir berhasil (LAN diutamakan, lalu Tailscale)

    static void init(Context c) {
        if (sp == null) sp = c.getApplicationContext().getSharedPreferences("zaxlet", Context.MODE_PRIVATE);
        if (sp.getString("id", null) == null) sp.edit().putString("id", java.util.UUID.randomUUID().toString()).apply();
    }
    static String get(String k) { return sp.getString(k, ""); }
    static boolean paired() { return !get("t").isEmpty() && (!get("lan").isEmpty() || !get("ts").isEmpty()); }
    static String id() { return get("id"); }
    static String name() { return android.os.Build.MODEL; }

    /** zaxlet://pair?lan=..&ts=..&p=..&tp=..&t=..&f=.. */
    static boolean savePairing(String link) {
        try {
            android.net.Uri u = android.net.Uri.parse(link.trim());
            if (!"zaxlet".equals(u.getScheme()) || u.getQueryParameter("t") == null) return false;
            sp.edit().putString("lan", nz(u.getQueryParameter("lan"))).putString("ts", nz(u.getQueryParameter("ts")))
              .putString("tp", nz(u.getQueryParameter("tp"))).putString("t", u.getQueryParameter("t")).putString("f", nz(u.getQueryParameter("f"))).apply();
            active = null;
            return true;
        } catch (Exception e) { return false; }
    }
    /** Paket yang notifikasinya boleh diteruskan ke PC. Bawaan kosong: semua mati sampai dinyalakan manual di layar Notifikasi ke PC. */
    static java.util.Set<String> allowed() { return new java.util.HashSet<>(sp.getStringSet("allowed", new java.util.HashSet<String>())); }
    static void setAllowed(String pkg, boolean on) {
        java.util.Set<String> s = allowed();
        if (on) s.add(pkg); else s.remove(pkg);
        sp.edit().putStringSet("allowed", s).apply();
    }

    static String nz(String s) { return s == null ? "" : s; }

    static SSLSocketFactory factory() throws Exception {
        final String fp = get("f").toLowerCase();
        TrustManager tm = new X509TrustManager() {
            public void checkClientTrusted(X509Certificate[] c, String a) {}
            public void checkServerTrusted(X509Certificate[] c, String a) throws CertificateException {
                try {
                    byte[] h = MessageDigest.getInstance("SHA-256").digest(c[0].getEncoded());
                    StringBuilder sb = new StringBuilder();
                    for (byte b : h) sb.append(String.format("%02x", b));
                    if (fp.isEmpty() || !sb.toString().equals(fp)) throw new CertificateException("sidik jari sertifikat PC tidak cocok");
                } catch (CertificateException e) { throw e; } catch (Exception e) { throw new CertificateException(e); }
            }
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
        };
        SSLContext ctx = SSLContext.getInstance("TLS");
        ctx.init(null, new TrustManager[]{tm}, new SecureRandom());
        return ctx.getSocketFactory();
    }

    static int port() { try { return Integer.parseInt(get("tp")); } catch (Exception e) { return 47891; } }

    /** Soket TLS ke PC: coba host yang terakhir berhasil, lalu LAN, lalu Tailscale. */
    static SSLSocket connect() throws Exception {
        String[] order = { active, get("lan"), get("ts") };
        StringBuilder why = new StringBuilder();   // semua alamat yang gagal, bukan hanya yang terakhir
        SSLSocketFactory f = factory();
        for (String h : order) {
            if (h == null || h.isEmpty() || why.indexOf(h + ":") >= 0) continue;
            try {
                Socket raw = new Socket();
                raw.connect(new InetSocketAddress(h, port()), 2500);
                SSLSocket s = (SSLSocket) f.createSocket(raw, h, port(), true);
                s.setSoTimeout(0);
                s.startHandshake();
                active = h;
                return s;
            } catch (Exception e) {
                String m = e instanceof java.net.SocketTimeoutException ? "timeout" : e.getMessage() == null ? e.getClass().getSimpleName() : e.getMessage();
                why.append(why.length() > 0 ? " | " : "").append(h).append(": ").append(m);
            }
        }
        throw new IOException(why.length() == 0 ? "tidak ada alamat" : why.toString());
    }

    /** Koneksi HTTPS satu permintaan (unggah/unduh) lewat host yang aktif. */
    static HttpsURLConnection http(String path, String method) throws Exception {
        if (active == null) connect().close();
        String sep = path.contains("?") ? "&" : "?";
        URL url = new URL("https://" + active + ":" + port() + path + sep + "t=" + get("t") + "&d=" + id() + "&dn=" + java.net.URLEncoder.encode(name(), "UTF-8"));
        HttpsURLConnection c = (HttpsURLConnection) url.openConnection();
        c.setSSLSocketFactory(factory());
        c.setHostnameVerifier((h, s) -> true);   // identitas dipastikan oleh sidik jari, bukan nama host
        c.setConnectTimeout(4000);
        c.setRequestMethod(method);
        return c;
    }
}
