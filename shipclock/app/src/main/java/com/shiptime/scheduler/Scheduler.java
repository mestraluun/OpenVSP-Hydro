package com.shiptime.scheduler;

import android.app.AlarmManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;

import java.util.List;

/** AlarmManager wrapper. */
final class Scheduler {
    static final String EXTRA_ID = "id";

    private Scheduler() {}

    static boolean canExact(Context ctx) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.S) return true;
        return ctx.getSystemService(AlarmManager.class).canScheduleExactAlarms();
    }

    static void schedule(Context ctx, Change c) {
        if (!Change.PENDING.equals(c.state)) return;
        AlarmManager am = ctx.getSystemService(AlarmManager.class);
        PendingIntent pi = alarmIntent(ctx, c.id);
        if (canExact(ctx)) {
            am.setExactAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, c.time, pi);
        } else {
            am.setAndAllowWhileIdle(AlarmManager.RTC_WAKEUP, c.time, pi);
        }
    }

    static void cancel(Context ctx, long id) {
        ctx.getSystemService(AlarmManager.class).cancel(alarmIntent(ctx, id));
    }

    /**
     * Re-arms all future pending changes. Of the pending changes already due, the latest
     * is fired now and older ones are marked skipped.
     */
    static void syncAll(Context ctx) {
        long now = System.currentTimeMillis();
        List<Change> list = Store.load(ctx);
        Change latestDue = null;
        for (Change c : list) {
            if (Change.PENDING.equals(c.state) && c.time <= now) latestDue = c;
        }
        for (Change c : list) {
            if (Change.PENDING.equals(c.state) && c.time > now) schedule(ctx, c);
        }
        if (latestDue != null) Reminder.fire(ctx, latestDue.id);
    }

    private static PendingIntent alarmIntent(Context ctx, long id) {
        Intent i = new Intent(ctx, AlarmReceiver.class)
                .setData(Uri.parse("shipclock://change/" + id))
                .putExtra(EXTRA_ID, id);
        return PendingIntent.getBroadcast(ctx, Long.hashCode(id), i,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
    }
}
