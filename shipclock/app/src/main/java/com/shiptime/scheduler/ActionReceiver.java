package com.shiptime.scheduler;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

import java.util.List;

/** Handles the notification's "Done" button. */
public class ActionReceiver extends BroadcastReceiver {
    static final String ACTION_DONE = "com.shiptime.scheduler.DONE";

    @Override
    public void onReceive(Context ctx, Intent intent) {
        if (!ACTION_DONE.equals(intent.getAction())) return;
        long id = intent.getLongExtra(Scheduler.EXTRA_ID, -1);
        List<Change> list = Store.load(ctx);
        Change c = Store.find(list, id);
        if (c == null) return;
        c.state = Change.DONE;
        Scheduler.cancel(ctx, c.id);
        Reminder.cancel(ctx, c);
        Store.save(ctx, list);
    }
}
