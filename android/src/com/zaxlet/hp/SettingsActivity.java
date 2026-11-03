package com.zaxlet.hp;

import android.app.Activity;
import android.content.Intent;
import android.graphics.Color;
import android.graphics.drawable.GradientDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.provider.Settings;
import android.view.Gravity;
import android.view.View;
import android.widget.*;

/** Setelan: izin jalan di latar, akses notifikasi, dan pilihan aplikasi yang notifikasinya diteruskan ke PC. */
public class SettingsActivity extends Activity {
    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        getWindow().setStatusBarColor(Color.parseColor("#0B0B0D"));
        getWindow().setNavigationBarColor(Color.parseColor("#0B0B0D"));
        ScrollView sv = new ScrollView(this);
        sv.setBackgroundColor(Color.parseColor("#0B0B0D"));
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(dp(20), dp(36), dp(20), dp(24));
        sv.addView(root);

        TextView t = new TextView(this);
        t.setText("Setelan");
        t.setTextSize(24); t.setTextColor(Color.WHITE); t.setTypeface(null, android.graphics.Typeface.BOLD);
        root.addView(t);

        root.addView(btn("Izinkan jalan di latar (hemat baterai)", v -> {
            try { startActivity(new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:" + getPackageName()))); }
            catch (Exception e) { startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)); }
        }));
        root.addView(btn("Izinkan akses notifikasi", v -> startActivity(new Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))));
        root.addView(btn("Pilih aplikasi notifikasi", v -> startActivity(new Intent(this, AppsActivity.class))));
        setContentView(sv);
    }

    private int dp(int v) { return (int) (v * getResources().getDisplayMetrics().density); }

    private Button btn(String s, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(s); b.setAllCaps(false); b.setTextColor(Color.WHITE); b.setTextSize(15);
        GradientDrawable g = new GradientDrawable(); g.setColor(Color.parseColor("#1C1C20")); g.setCornerRadius(dp(14));
        b.setBackground(g); b.setPadding(dp(14), dp(14), dp(14), dp(14)); b.setOnClickListener(l); b.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams p = new LinearLayout.LayoutParams(-1, -2); p.topMargin = dp(12);
        b.setLayoutParams(p);
        return b;
    }
}
