package com.zaxlet.hp;

import android.app.Notification;
import android.app.RemoteInput;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.os.Parcelable;
import android.service.notification.NotificationListenerService;
import android.service.notification.StatusBarNotification;
import android.util.Base64;
import java.io.ByteArrayOutputStream;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import org.json.JSONObject;

/**
 * Forwards phone notifications to the PC via the control channel (ConnService). Updates to an existing notification (same Key)
 * (e.g., Gojek delivery status) are re-sent as updates. Reply actions (RemoteInput) are stored so the PC can reply to chats.
 * Requires "Notification access" permission granted by the user in Android Settings.
 */
public class NotifService extends NotificationListenerService {
    static volatile NotifService inst;
    static volatile int seen, sent;           // diagnostics: notifications seen / successfully sent to PC
    static volatile String lastErr = "";
    static final Map<String, Notification.Action> replies = new ConcurrentHashMap<>();
    static final Set<String> iconSent = ConcurrentHashMap.newKeySet();
    static final Set<String> calls = ConcurrentHashMap.newKeySet();   // active call notification keys to avoid repeated triggers
    private static final ExecutorService io = Executors.newSingleThreadExecutor();   // networking must not run on the main thread

    @Override public void onListenerConnected() {
        Net.init(this);   // process can be started by the system without opening the app: ensure pairing settings are loaded
        inst = this;
        ConnService.ensure(this);   // system binds this service: anchor to start ConnService if it is down
        resync();
    }

    @Override public void onListenerDisconnected() {
        inst = null;
        calls.clear();
        try { requestRebind(new android.content.ComponentName(this, NotifService.class)); } catch (Exception e) { }
    }

    /** Re-send all active notifications (called when a PC connection is newly established). */
    static void resync() {
        final NotifService s = inst;
        if (s == null) return;
        iconSent.clear();
        io.execute(() -> {
            try { for (StatusBarNotification n : s.getActiveNotifications()) s.post(n); } catch (Exception e) { }
        });
    }

    @Override public void onNotificationPosted(final StatusBarNotification sbn) { io.execute(() -> post(sbn)); }

    @Override public void onNotificationRemoved(final StatusBarNotification sbn) {
        io.execute(() -> {
            String k = sbn.getKey();
            replies.remove(k);
            if (calls.remove(k) && calls.isEmpty()) {
                try { ConnService.send(new JSONObject().put("t", "c").put("on", false).toString()); } catch (Exception e) { }
            }
            try { ConnService.send(new JSONObject().put("t", "r").put("k", k).toString()); } catch (Exception e) { }
        });
    }

    private void post(StatusBarNotification sbn) {
        try {
            Net.init(this);
            seen++;
            if (!ConnService.alive) ConnService.ensure(this);
            if (!Net.paired() || sbn.getPackageName().equals(getPackageName())) return;
            Notification n = sbn.getNotification();
            if ((n.flags & Notification.FLAG_GROUP_SUMMARY) != 0) return;
            if (Notification.CATEGORY_CALL.equals(n.category)) {
                String k = sbn.getKey();
                if (!calls.contains(k)) {
                    boolean first = calls.isEmpty();
                    if (calls.add(k) && first) {
                        if (!ConnService.send(new JSONObject().put("t", "c").put("on", true).toString())) calls.remove(k);
                    }
                }
            }
            if (Notification.CATEGORY_TRANSPORT.equals(n.category)) return;   // music player: already handled by ZaxLet via SMTC on PC
            Bundle e = n.extras;
            String title = str(e.getCharSequence(Notification.EXTRA_TITLE));
            String text = str(e.getCharSequence(Notification.EXTRA_BIG_TEXT));
            if (text.isEmpty()) text = str(e.getCharSequence(Notification.EXTRA_TEXT));
            Parcelable[] msgs = e.getParcelableArray(Notification.EXTRA_MESSAGES);   // chat: take the last message
            if (msgs != null && msgs.length > 0 && msgs[msgs.length - 1] instanceof Bundle) {
                Bundle last = (Bundle) msgs[msgs.length - 1];
                String t = str(last.getCharSequence("text"));
                if (!t.isEmpty()) text = t;
            }
            if (title.isEmpty() && text.isEmpty()) return;
            org.json.JSONArray ma = new org.json.JSONArray();   // last few messages of the conversation (PC displays them as lines)
            if (msgs != null)
                for (int q = Math.max(0, msgs.length - 8); q < msgs.length; q++) {
                    if (!(msgs[q] instanceof Bundle)) continue;
                    Bundle mb = (Bundle) msgs[q];
                    String mt = str(mb.getCharSequence("text"));
                    if (!mt.isEmpty()) ma.put(new JSONObject().put("s", cut(str(mb.getCharSequence("sender")), 60)).put("t", cut(mt, 300)));
                }

            String pkg = sbn.getPackageName();
            if (!Net.allowed().contains(pkg)) return;   // all off by default; enabled manually in Settings > Choose notification apps
            PackageManager pm = getPackageManager();
            String app = pkg;
            try { app = str(pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0))); } catch (Exception ex) { }
            if (iconSent.add(pkg)) sendIcon(pkg, app);

            boolean canReply = false;
            if (n.actions != null)
                for (Notification.Action a : n.actions)
                    if (a.getRemoteInputs() != null && a.getRemoteInputs().length > 0) { replies.put(sbn.getKey(), a); canReply = true; break; }

            JSONObject j = new JSONObject().put("t", "n").put("k", sbn.getKey()).put("pkg", pkg).put("app", app)
                    .put("title", cut(title, 120)).put("text", cut(text, 400)).put("og", (n.flags & Notification.FLAG_ONGOING_EVENT) != 0).put("rp", canReply);
            if (ma.length() > 0) j.put("ms", ma);
            int max = e.getInt(Notification.EXTRA_PROGRESS_MAX, 0);
            if (max > 0) j.put("pc", e.getInt(Notification.EXTRA_PROGRESS, 0)).put("pm", max);
            if (ConnService.send(j.toString())) { sent++; lastErr = ""; } else lastErr = "belum tersambung ke PC";
        } catch (Exception ex) { lastErr = String.valueOf(ex); }
    }

    private void sendIcon(String pkg, String app) {
        try {
            Drawable d = getPackageManager().getApplicationIcon(pkg);
            Bitmap b = Bitmap.createBitmap(48, 48, Bitmap.Config.ARGB_8888);
            Canvas c = new Canvas(b);
            d.setBounds(0, 0, 48, 48);
            d.draw(c);
            ByteArrayOutputStream bo = new ByteArrayOutputStream();
            b.compress(Bitmap.CompressFormat.PNG, 100, bo);
            ConnService.send(new JSONObject().put("t", "i").put("pkg", pkg).put("app", app)
                    .put("png", Base64.encodeToString(bo.toByteArray(), Base64.NO_WRAP)).toString());
        } catch (Exception ex) { }
    }

    /** Reply to chat: trigger the notification's RemoteInput action as if the user typed in the notification shade. */
    static void reply(final String key, final String text) {
        final NotifService s = inst;
        final Notification.Action a = replies.get(key);
        if (s == null || a == null) return;
        try {
            RemoteInput[] ris = a.getRemoteInputs();
            Intent i = new Intent();
            Bundle res = new Bundle();
            for (RemoteInput ri : ris) res.putCharSequence(ri.getResultKey(), text);
            RemoteInput.addResultsToIntent(ris, i, res);
            a.actionIntent.send(s, 0, i);
        } catch (Exception e) { }
    }

    private static String str(CharSequence c) { return c == null ? "" : c.toString().trim(); }
    private static String cut(String s, int n) { return s.length() > n ? s.substring(0, n) : s; }
}
