package com.zaxlet.hp;

import android.app.Activity;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Color;
import android.graphics.drawable.Drawable;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.view.Gravity;
import android.widget.*;
import java.util.ArrayList;
import java.util.Collections;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

/** Pilih aplikasi yang notifikasinya diteruskan ke PC (seperti daftar notifikasi aplikasi di jam pintar). Bawaan: semua mati. */
public class AppsActivity extends Activity {
    private static final class Item { String pkg, label; Drawable icon; }

    private final Handler ui = new Handler(Looper.getMainLooper());
    private LinearLayout list;

    @Override protected void onCreate(Bundle b) {
        super.onCreate(b);
        Net.init(this);
        LinearLayout root = Ui.page(this, "Notifikasi ke PC");
        TextView hint = Ui.text(this, "Semua mati secara bawaan. Aktifkan aplikasi yang notifikasinya boleh muncul di pill PC.", 13, Ui.DIM, false);
        hint.setPadding(0, dp(6), 0, 0);
        root.addView(hint);
        list = Ui.card(root, null);

        new Thread(() -> {   // memuat ikon puluhan aplikasi: jangan di main thread
            final List<Item> items = load();
            ui.post(() -> show(items));
        }).start();
    }

    private List<Item> load() {
        PackageManager pm = getPackageManager();
        Intent i = new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER);
        Set<String> seen = new HashSet<>();
        List<Item> out = new ArrayList<>();
        for (ResolveInfo r : pm.queryIntentActivities(i, 0)) {
            String pkg = r.activityInfo.packageName;
            if (pkg.equals(getPackageName()) || !seen.add(pkg)) continue;
            Item it = new Item();
            it.pkg = pkg; it.label = String.valueOf(r.loadLabel(pm)); it.icon = r.loadIcon(pm);
            out.add(it);
        }
        Collections.sort(out, (a, b) -> a.label.compareToIgnoreCase(b.label));
        return out;
    }

    private void show(List<Item> items) {
        final java.util.Set<String> allowed = Net.allowed();
        for (Item it : items) {
            LinearLayout row = new LinearLayout(this);
            row.setOrientation(LinearLayout.HORIZONTAL);
            row.setGravity(Gravity.CENTER_VERTICAL);
            row.setPadding(0, dp(8), 0, dp(8));
            ImageView iv = new ImageView(this);
            iv.setImageDrawable(it.icon);
            row.addView(iv, new LinearLayout.LayoutParams(dp(38), dp(38)));
            TextView tv = new TextView(this);
            tv.setText(it.label); tv.setTextSize(16); tv.setTextColor(Color.WHITE);
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, -2, 1f);
            lp.leftMargin = dp(14);
            row.addView(tv, lp);
            Switch sw = new Switch(this);
            sw.setChecked(allowed.contains(it.pkg));
            final String pkg = it.pkg;
            sw.setOnCheckedChangeListener((v, on) -> Net.setAllowed(pkg, on));
            row.addView(sw);
            list.addView(row);
        }
    }

    private int dp(int v) { return (int) (v * getResources().getDisplayMetrics().density); }
}
