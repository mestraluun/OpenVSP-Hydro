package com.shiptime.scheduler;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.net.Uri;
import android.provider.Settings;

import java.util.List;

/** Posts and clears the "change clocks" notification. */
final class Reminder {
    static final String CHANNEL = "clock_changes";

    private Reminder() {}

    /** Called when a change is due. Older still-open changes are superseded and skipped. */
    static void fire(Context ctx, long id) {
        long now = System.currentTimeMillis();
        List<Change> list = Store.load(ctx);
        Change c = Store.find(list, id);
        if (c == null || !c.isActive()) return;

        for (Change o : list) {
            if (o != c && o.isActive() && o.time <= c.time) {
                o.state = Change.SKIPPED;
                Scheduler.cancel(ctx, o.id);
                cancel(ctx, o);
            }
        }

        if (Zones.matches(Zones.phoneZone(), c.zone, now)) {
            c.state = Change.DONE;
            cancel(ctx, c);
        } else {
            c.state = Change.REMINDED;
            post(ctx, c);
        }
        Store.save(ctx, list);
    }

    static void cancel(Context ctx, Change c) {
        ctx.getSystemService(NotificationManager.class).cancel(c.notificationId());
    }

    static void ensureChannel(Context ctx) {
        NotificationChannel ch = new NotificationChannel(CHANNEL, "Clock change reminders",
                NotificationManager.IMPORTANCE_HIGH);
        ch.setDescription("Reminds you to change the phone's time zone");
        ch.enableVibration(true);
        ctx.getSystemService(NotificationManager.class).createNotificationChannel(ch);
    }

    static Intent settingsIntent() {
        return new Intent(Settings.ACTION_DATE_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
    }

    private static void post(Context ctx, Change c) {
        ensureChannel(ctx);
        long now = System.currentTimeMillis();
        int minutes = Zones.offsetMinutes(c.zone, now);
        String title = "Change clocks to " + Zones.describe(c.zone, now);
        String text = Zones.isEtc(c.zone)
                ? "Time zone > UTC offset > " + Zones.gmtLabel(minutes)
                : "Time zone > " + c.zone + " (" + Zones.gmtLabel(minutes) + ")";

        PendingIntent settings = PendingIntent.getActivity(ctx, c.notificationId(),
                settingsIntent(), PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Intent done = new Intent(ctx, ActionReceiver.class)
                .setAction(ActionReceiver.ACTION_DONE)
                .setData(Uri.parse("shipclock://done/" + c.id))
                .putExtra(Scheduler.EXTRA_ID, c.id);
        PendingIntent donePi = PendingIntent.getBroadcast(ctx, c.notificationId(), done,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification n = new Notification.Builder(ctx, CHANNEL)
                .setSmallIcon(R.drawable.ic_notif)
                .setContentTitle(title)
                .setContentText(text)
                .setStyle(new Notification.BigTextStyle().bigText(text))
                .setCategory(Notification.CATEGORY_REMINDER)
                .setOngoing(true)
                .setAutoCancel(false)
                .setWhen(c.time)
                .setShowWhen(true)
                .setContentIntent(settings)
                .addAction(new Notification.Action.Builder(null, "Open settings", settings).build())
                .addAction(new Notification.Action.Builder(null, "Done", donePi).build())
                .build();
        ctx.getSystemService(NotificationManager.class).notify(c.notificationId(), n);
    }
}
