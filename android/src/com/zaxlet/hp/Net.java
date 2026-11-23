package com.zaxlet.hp;

import android.content.Context;
import android.content.SharedPreferences;
import android.net.ConnectivityManager;
import java.io.*;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.net.URL;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import javax.net.ssl.*;

/** Pairing settings + TLS connection to PC. PC certificate is pinned via SHA-256 fingerprint from the pairing link. */
final class Net {
    static SharedPreferences sp;
    static Context app;
    static volatile String active;   // last successful host (LAN preferred, then Tailscale)

    static void init(Context c) {
        app = c.getApplicationContext();
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
    /** Packages whose notifications may be forwarded to the PC. Empty by default: all off until manually enabled on the Phone-to-PC Notifications screen. */
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

    /** Phone is connected to the laptop's hotspot (Wi-Fi): the gateway is the laptop itself, no re-pairing needed. Null if not Wi-Fi. */
    static String gateway() {
        try {
            ConnectivityManager cm = (ConnectivityManager) app.getSystemService(Context.CONNECTIVITY_SERVICE);
            android.net.Network n = cm.getActiveNetwork();
            android.net.NetworkCapabilities nc = cm.getNetworkCapabilities(n);
            if (nc == null || !nc.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI)) return null;
            for (android.net.RouteInfo r : cm.getLinkProperties(n).getRoutes())
                if (r.isDefaultRoute() && r.getGateway() instanceof java.net.Inet4Address) return r.getGateway().getHostAddress();
        } catch (Exception e) { }
        return null;
    }

    /**
     * Phone sharing hotspot/USB tethering to the laptop: the laptop's IP is not fixed, so scan the tether interface subnet (not the phone's internet network)
     * and return hosts with the ZaxLet port open. Empty if not currently tethering, so it does not slow down normal connections.
     */
    static java.util.List<String> tetherHosts() {
        java.util.List<String> found = new java.util.concurrent.CopyOnWriteArrayList<>();
        try {
            ConnectivityManager cm = (ConnectivityManager) app.getSystemService(Context.CONNECTIVITY_SERVICE);
            android.net.LinkProperties lp = cm.getLinkProperties(cm.getActiveNetwork());
            String def = lp == null ? null : lp.getInterfaceName();
            java.util.concurrent.ExecutorService ex = java.util.concurrent.Executors.newFixedThreadPool(48);
            for (java.net.NetworkInterface ni : java.util.Collections.list(java.net.NetworkInterface.getNetworkInterfaces())) {
                if (!ni.isUp() || ni.isLoopback() || ni.getName().equals(def)) continue;
                for (java.net.InterfaceAddress ia : ni.getInterfaceAddresses()) {
                    if (!(ia.getAddress() instanceof java.net.Inet4Address) || !ia.getAddress().isSiteLocalAddress()) continue;
                    final byte[] b = ia.getAddress().getAddress();
                    for (int i = 1; i < 255; i++) {
                        if (i == (b[3] & 0xFF)) continue;
                        final String h = (b[0] & 0xFF) + "." + (b[1] & 0xFF) + "." + (b[2] & 0xFF) + "." + i;
                        ex.execute(() -> {
                            try (Socket s = new Socket()) { s.connect(new InetSocketAddress(h, port()), 250); found.add(h); } catch (Exception e) { }
                        });
                    }
                }
            }
            ex.shutdown();
            ex.awaitTermination(6, java.util.concurrent.TimeUnit.SECONDS);
            ex.shutdownNow();
        } catch (Exception e) { }
        return found;
    }

    /** TLS socket to PC: last successful host, laptop hotspot gateway, LAN, laptop on phone hotspot (scan), then Tailscale. */
    static SSLSocket connect() throws Exception {
        String gw = gateway();
        String[] order = { active, "127.0.0.1", gw, get("lan"), "*tether*", get("ts") };   // 127.0.0.1 = USB tunnel (adb reverse), free of Wi-Fi jitter; fails immediately if absent
        StringBuilder why = new StringBuilder();   // all failed addresses, not just the last one
        SSLSocketFactory f = factory();
        java.util.LinkedList<String> hosts = new java.util.LinkedList<>(java.util.Arrays.asList(order));
        while (!hosts.isEmpty()) {
            String h = hosts.removeFirst();
            if ("*tether*".equals(h)) { hosts.addAll(0, tetherHosts()); continue; }   // scan only if previous addresses failed
            if (h == null || h.isEmpty() || why.indexOf(h + ":") >= 0) continue;
            try {
                Socket raw = new Socket();
                raw.setKeepAlive(true);
                raw.setTcpNoDelay(true);   // without this, Nagle + delayed ACK hold small packets (audio/camera) for tens of ms
                raw.connect(new InetSocketAddress(h, port()), h.equals(gw) || h.equals("127.0.0.1") ? 600 : 2500);   // gateway is usually a router: don't wait long if it's not the laptop
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

    /** Single-request HTTPS connection (upload/download) via the active host. */
    static HttpsURLConnection http(String path, String method) throws Exception {
        String host = active;
        if (host == null) { connect().close(); host = active; }
        if (host == null) throw new IOException("tidak ada alamat");
        String sep = path.contains("?") ? "&" : "?";
        URL url = new URL("https://" + host + ":" + port() + path + sep + "t=" + get("t") + "&d=" + id() + "&dn=" + java.net.URLEncoder.encode(name(), "UTF-8"));
        HttpsURLConnection c = (HttpsURLConnection) url.openConnection();
        c.setSSLSocketFactory(factory());
        c.setHostnameVerifier((h, s) -> true);   // identity is verified by fingerprint, not hostname
        c.setConnectTimeout(4000);
        c.setReadTimeout(30000);
        c.setRequestMethod(method);
        return c;
    }
}
