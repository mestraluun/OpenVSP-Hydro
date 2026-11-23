package com.shiptime.scheduler;

import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;

public class AlarmReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context ctx, Intent intent) {
        long id = intent.getLongExtra(Scheduler.EXTRA_ID, -1);
        if (id != -1) Reminder.fire(ctx, id);
    }
}
