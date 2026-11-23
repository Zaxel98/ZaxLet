package com.zaxlet.hp;

import android.app.Activity;
import android.content.Context;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.view.Gravity;
import android.view.View;
import android.widget.*;

/** Shared phone screen styling: dark, rounded cards, teal accent (matching the PC island). */
final class Ui {
    static final int BG = 0xFF0B0B0D, CARD = 0xFF17171B, FIELD = 0xFF222228, TEXT = 0xFFFFFFFF, DIM = 0xFFA0A0A8,
            ACC = 0xFF2DD4BF, ON_ACC = 0xFF04201C, WARN = 0xFFFBBF24, BAD = 0xFFF87171;

    static int dp(Context c, int v) { return (int) (v * c.getResources().getDisplayMetrics().density + 0.5f); }

    static GradientDrawable shape(Context c, int color, int radiusDp) {
        GradientDrawable g = new GradientDrawable();
        g.setColor(color); g.setCornerRadius(dp(c, radiusDp));
        return g;
    }

    /** Dark-styled scroll page; returns the vertical container where content is added. */
    static LinearLayout page(Activity a, String title) {
        a.getWindow().setStatusBarColor(BG);
        a.getWindow().setNavigationBarColor(BG);
        ScrollView sv = new ScrollView(a);
        sv.setBackgroundColor(BG);
        sv.setFillViewport(true);
        LinearLayout root = new LinearLayout(a);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(a, 16), dp(a, 28), dp(a, 16), dp(a, 28));
        sv.addView(root);
        a.setContentView(sv);
        root.addView(text(a, title, 26, TEXT, true));
        return root;
    }

    static TextView text(Context c, String s, int sp, int color, boolean bold) {
        TextView t = new TextView(c);
        t.setText(s); t.setTextSize(sp); t.setTextColor(color);
        if (bold) t.setTypeface(null, android.graphics.Typeface.BOLD);
        return t;
    }

    /** Card: small title on top, content added to the returned LinearLayout. */
    static LinearLayout card(LinearLayout parent, String heading) {
        Context c = parent.getContext();
        if (heading != null) {
            TextView h = text(c, heading, 12, DIM, true);
            h.setAllCaps(true); h.setLetterSpacing(0.08f);
            LinearLayout.LayoutParams hp = new LinearLayout.LayoutParams(-1, -2);
            hp.topMargin = dp(c, 20); hp.bottomMargin = dp(c, 8); hp.leftMargin = dp(c, 4);
            parent.addView(h, hp);
        }
        LinearLayout k = new LinearLayout(c);
        k.setOrientation(LinearLayout.VERTICAL);
        k.setBackground(shape(c, CARD, 18));
        k.setPadding(dp(c, 16), dp(c, 14), dp(c, 16), dp(c, 14));
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
        if (heading == null) p.topMargin = dp(c, 16);
        parent.addView(k, p);
        return k;
    }

    /** Wide button (primary = teal) or dark secondary button; minimum height 48dp for easy tapping. */
    static Button button(Context c, String s, boolean primary, View.OnClickListener l) {
        Button b = new Button(c);
        b.setText(s); b.setAllCaps(false); b.setTextSize(15); b.setStateListAnimator(null);
        b.setTextColor(primary ? ON_ACC : TEXT);
        b.setBackground(shape(c, primary ? ACC : FIELD, 14));
        b.setMinHeight(dp(c, 48)); b.setMinimumHeight(dp(c, 48));
        b.setOnClickListener(l); b.setGravity(Gravity.CENTER);
        return b;
    }

    static LinearLayout.LayoutParams gap(Context c, int topDp) {
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2);
        p.topMargin = dp(c, topDp);
        return p;
    }

    static EditText field(Context c, String hint, int minLines) {
        EditText e = new EditText(c);
        e.setHint(hint); e.setTextColor(TEXT); e.setHintTextColor(0xFF70707A); e.setTextSize(14);
        e.setBackground(shape(c, FIELD, 12));
        e.setPadding(dp(c, 12), dp(c, 10), dp(c, 12), dp(c, 10));
        if (minLines > 1) { e.setMinLines(minLines); e.setGravity(Gravity.TOP); }
        return e;
    }

    static View dot(Context c, int color) {
        View v = new View(c);
        GradientDrawable g = new GradientDrawable();
        g.setShape(GradientDrawable.OVAL); g.setColor(color);
        v.setBackground(g);
        return v;
    }

    static void tint(View dot, int color) { ((GradientDrawable) dot.getBackground()).setColor(color); }
}
