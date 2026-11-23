package com.zaxlet.hp;

import android.accessibilityservice.AccessibilityService;
import android.accessibilityservice.GestureDescription;
import android.content.Intent;
import android.graphics.Path;
import android.graphics.Rect;
import android.os.Build;
import android.os.Bundle;
import android.util.DisplayMetrics;
import android.view.WindowManager;
import android.view.accessibility.AccessibilityEvent;
import android.view.accessibility.AccessibilityNodeInfo;
import org.json.JSONObject;

/** Accessibility service to forward taps, swipes, navigation buttons, and text from the laptop to the phone screen. */
public class ZaxAccess extends AccessibilityService {
    static volatile ZaxAccess instance;

    @Override protected void onServiceConnected() {
        super.onServiceConnected();
        instance = this;
    }

    @Override public boolean onUnbind(Intent intent) {
        instance = null;
        return super.onUnbind(intent);
    }

    @Override public void onDestroy() {
        instance = null;
        super.onDestroy();
    }

    @Override public void onAccessibilityEvent(AccessibilityEvent event) { }

    @Override public void onInterrupt() { }

    void perform(JSONObject o) {
        if (o == null) return;
        String t = o.optString("t", "");
        if ("key".equals(t)) {
            String k = o.optString("k", "");
            if ("back".equals(k)) performGlobalAction(GLOBAL_ACTION_BACK);
            else if ("home".equals(k)) performGlobalAction(GLOBAL_ACTION_HOME);
            else if ("recents".equals(k)) performGlobalAction(GLOBAL_ACTION_RECENTS);
            else if ("notifications".equals(k)) performGlobalAction(GLOBAL_ACTION_NOTIFICATIONS);
            return;
        }
        if ("text".equals(t)) {
            appendText(o.optString("s", ""));
            return;
        }
        int[] sz = screenSize();
        int w = sz[0], h = sz[1];
        if (w <= 0 || h <= 0) return;

        if ("tap".equals(t) || "press".equals(t)) {
            float x = clamp((float) (clamp01(o.optDouble("x", 0.5)) * w), 1f, w - 1f);
            float y = clamp((float) (clamp01(o.optDouble("y", 0.5)) * h), 1f, h - 1f);
            long ms = "press".equals(t) ? clampLong(o.optLong("ms", 600), 50, 5000) : 50;
            Path p = new Path();
            p.moveTo(x, y);
            dispatch(p, ms);
        } else if ("swipe".equals(t)) {
            float x1 = clamp((float) (clamp01(o.optDouble("x1", 0.5)) * w), 1f, w - 1f);
            float y1 = clamp((float) (clamp01(o.optDouble("y1", 0.5)) * h), 1f, h - 1f);
            float x2 = clamp((float) (clamp01(o.optDouble("x2", 0.5)) * w), 1f, w - 1f);
            float y2 = clamp((float) (clamp01(o.optDouble("y2", 0.5)) * h), 1f, h - 1f);
            long ms = clampLong(o.optLong("ms", 300), 50, 3000);
            Path p = new Path();
            p.moveTo(x1, y1);
            p.lineTo(x2, y2);
            dispatch(p, ms);
        } else if ("scroll".equals(t)) {
            float x = clamp((float) (clamp01(o.optDouble("x", 0.5)) * w), 1f, w - 1f);
            float y = clamp((float) (clamp01(o.optDouble("y", 0.5)) * h), 1f, h - 1f);
            int dy = o.optInt("dy", 0);
            if (dy == 0) return;
            float dist = Math.max(40f, h * 0.15f);
            float y2 = clamp(y + (dy > 0 ? -dist : dist), 1f, h - 1f);
            Path p = new Path();
            p.moveTo(x, y);
            p.lineTo(x, y2);
            dispatch(p, 180);
        }
    }

    private void dispatch(Path p, long ms) {
        GestureDescription gd = new GestureDescription.Builder()
                .addStroke(new GestureDescription.StrokeDescription(p, 0, ms))
                .build();
        dispatchGesture(gd, null, null);
    }

    private void appendText(String s) {
        if (s == null || s.isEmpty()) return;
        AccessibilityNodeInfo root = getRootInActiveWindow();
        if (root == null) return;
        AccessibilityNodeInfo focused = null;
        try {
            focused = root.findFocus(AccessibilityNodeInfo.FOCUS_INPUT);
            if (focused == null || !focused.isEditable()) return;
            CharSequence cur = focused.isShowingHintText() ? null : focused.getText();
            String next = (cur == null ? "" : cur.toString()) + s;
            Bundle args = new Bundle();
            args.putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, next);
            focused.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args);
        } finally {
            if (focused != null) focused.recycle();
            root.recycle();
        }
    }

    private int[] screenSize() {
        WindowManager wm = (WindowManager) getSystemService(WINDOW_SERVICE);
        if (wm == null) return new int[]{0, 0};
        if (Build.VERSION.SDK_INT >= 30) {
            Rect b = wm.getCurrentWindowMetrics().getBounds();
            return new int[]{b.width(), b.height()};
        }
        DisplayMetrics dm = new DisplayMetrics();
        wm.getDefaultDisplay().getRealMetrics(dm);
        return new int[]{dm.widthPixels, dm.heightPixels};
    }

    private static double clamp01(double v) { return Math.max(0.0, Math.min(1.0, v)); }
    private static float clamp(float v, float lo, float hi) { return Math.max(lo, Math.min(hi, v)); }
    private static long clampLong(long v, long lo, long hi) { return Math.max(lo, Math.min(hi, v)); }
}
