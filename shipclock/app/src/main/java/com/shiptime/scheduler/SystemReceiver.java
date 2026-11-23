package com.shiptime.scheduler;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

import java.util.List;

/** Boot / app update: reschedule. Time zone changed: clear reminders that now match. */
public class SystemReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        String action = intent.getAction();
        if (Intent.ACTION_TIMEZONE_CHANGED.equals(action)) {
            String tz = intent.getStringExtra(Intent.EXTRA_TIMEZONE);
            clearMatching(ctx, tz != null ? tz : Zones.phoneZone());
        } else if (Intent.ACTION_BOOT_COMPLETED.equals(action)
                || Intent.ACTION_MY_PACKAGE_REPLACED.equals(action)) {
            Scheduler.syncAll(ctx);
        }
    }

    /** The latest reminded change matching the new zone becomes DONE; older reminders are skipped. */
    static void clearMatching(Context ctx, String phoneZone) {
        long now = System.currentTimeMillis();
        List<Change> list = Store.load(ctx);
        Change match = null;
        for (Change c : list) {
            if (Change.REMINDED.equals(c.state) && Zones.matches(phoneZone, c.zone, now)) match = c;
        }
        if (match == null) return;
        for (Change c : list) {
            if (Change.REMINDED.equals(c.state) && c.time <= match.time) {
                c.state = c == match ? Change.DONE : Change.SKIPPED;
                Reminder.cancel(ctx, c);
            }
        }
        Store.save(ctx, list);
    }
}
