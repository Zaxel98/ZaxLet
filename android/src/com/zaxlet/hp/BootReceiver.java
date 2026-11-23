package com.zaxlet.hp;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

/** After the phone restarts or the app is updated: restart the connection service if the user had enabled it. */
public class BootReceiver extends BroadcastReceiver {
    @Override public void onReceive(Context c, Intent i) { ConnService.ensure(c); }
}
