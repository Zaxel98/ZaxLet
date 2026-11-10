package com.zaxlet.hp;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** Setelah HP restart atau aplikasi diperbarui: nyalakan lagi layanan sambungan bila pengguna pernah mengaktifkannya. */
public class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context c, Intent i) { ConnService.ensure(c); }
}
