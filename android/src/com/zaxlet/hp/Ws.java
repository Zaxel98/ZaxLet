package com.zaxlet.hp;

import java.io.*;
import java.security.SecureRandom;
import javax.net.ssl.SSLSocket;

/** Minimal WebSocket client (RFC 6455) over a TLS socket: sends binary/text, reads text frames from the server. */
final class Ws {
    final SSLSocket sock;
    final InputStream in;
    final OutputStream out;
    private final SecureRandom rnd = new SecureRandom();
    volatile boolean closed;

    Ws(String path) throws Exception {
        sock = Net.connect();
        in = new BufferedInputStream(sock.getInputStream());
        out = sock.getOutputStream();
        byte[] k = new byte[16];
        rnd.nextBytes(k);
        String req = "GET " + path + (path.contains("?") ? "&" : "?") + "t=" + Net.get("t") + "&d=" + Net.id() + "&dn=" + java.net.URLEncoder.encode(Net.name(), "UTF-8") + " HTTP/1.1\r\n"
                + "Host: " + Net.active + "\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n"
                + "Sec-WebSocket-Key: " + android.util.Base64.encodeToString(k, android.util.Base64.NO_WRAP) + "\r\n\r\n";
        out.write(req.getBytes("US-ASCII"));
        out.flush();
        StringBuilder sb = new StringBuilder();
        int a = 0, b;
        while (!sb.toString().endsWith("\r\n\r\n")) {
            b = in.read();
            if (b < 0 || ++a > 8192) throw new IOException("handshake gagal");
            sb.append((char) b);
        }
        if (!sb.toString().startsWith("HTTP/1.1 101")) throw new IOException("ditolak: " + sb.toString().split("\r\n")[0]);
    }

    synchronized void send(int op, byte[] p, int off, int len) throws IOException {
        if (closed) throw new IOException("ditutup");
        ByteArrayOutputStream h = new ByteArrayOutputStream(14);
        h.write(0x80 | op);
        if (len < 126) h.write(0x80 | len);
        else if (len < 65536) { h.write(0x80 | 126); h.write(len >> 8); h.write(len & 255); }
        else { h.write(0x80 | 127); for (int i = 7; i >= 0; i--) h.write((int) (((long) len >> (8 * i)) & 255)); }
        byte[] m = new byte[4];
        rnd.nextBytes(m);
        h.write(m);
        byte[] hb = h.toByteArray();
        byte[] fr = new byte[hb.length + len];   // single write: header and payload are not split into two TLS records
        System.arraycopy(hb, 0, fr, 0, hb.length);
        for (int i = 0; i < len; i++) fr[hb.length + i] = (byte) (p[off + i] ^ m[i & 3]);
        out.write(fr);
        out.flush();
    }

    void sendBinary(byte[] p, int len) throws IOException { send(2, p, 0, len); }
    void sendText(String s) throws IOException { byte[] b = s.getBytes("UTF-8"); send(1, b, 0, b.length); }

    /** Reads one frame; returns text if text frame, "" for other frames, null if closed. */
    String readText() throws IOException {
        int b0 = in.read();
        if (b0 < 0) return null;
        int op = b0 & 15;
        int b1 = in.read();
        if (b1 < 0) return null;
        int l = b1 & 0x7F;
        long len = l;
        if (l == 126) {
            int hi = in.read(), lo = in.read();
            if (hi < 0 || lo < 0) return null;
            len = (hi << 8) | lo;
        } else if (l == 127) {
            len = 0;
            for (int i = 0; i < 8; i++) {
                int b = in.read();
                if (b < 0) return null;
                len = (len << 8) | b;
            }
        }
        if (len < 0 || len > 65536) return null;
        byte[] d = new byte[(int) len];
        int got = 0;
        while (got < len) { int r = in.read(d, got, (int) len - got); if (r < 0) return null; got += r; }
        if (op == 8) return null;
        if (op == 9) { send(10, d, 0, d.length); return ""; }
        return op == 1 ? new String(d, "UTF-8") : "";
    }

    void close() { closed = true; try { sock.close(); } catch (Exception e) { } }
}
