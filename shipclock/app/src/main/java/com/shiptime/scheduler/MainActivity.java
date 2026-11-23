package com.shiptime.scheduler;

import android.app.Activity;
import android.app.AlertDialog;
import android.app.DatePickerDialog;
import android.app.TimePickerDialog;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Typeface;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.provider.Settings;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.ViewGroup;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import java.time.Instant;
import java.time.LocalDate;
import java.time.LocalDateTime;
import java.time.LocalTime;
import java.time.ZoneId;
import java.time.ZonedDateTime;
import java.time.format.DateTimeFormatter;
import java.util.ArrayList;
import java.util.List;
import java.util.Locale;

public class MainActivity extends Activity {
    private static final String NOTIF_PERMISSION = "android.permission.POST_NOTIFICATIONS";
    private static final DateTimeFormatter DATE = DateTimeFormatter.ofPattern("EEE d MMM", Locale.US);
    private static final DateTimeFormatter TIME = DateTimeFormatter.ofPattern("HH:mm", Locale.US);

    private LocalDate date;
    private LocalTime time;

    private TextView status;
    private LinearLayout warnings;
    private Button dateButton;
    private Button timeButton;
    private Spinner offsetSpinner;
    private EditText customZone;
    private TextView interpretHint;
    private LinearLayout listView;

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        Reminder.ensureChannel(this);

        LocalDateTime next = LocalDateTime.now().plusHours(1).withMinute(0).withSecond(0).withNano(0);
        date = next.toLocalDate();
        time = next.toLocalTime();

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        int pad = dp(16);
        root.setPadding(pad, pad, pad, pad);

        status = text(18, true);
        root.addView(status);

        warnings = new LinearLayout(this);
        warnings.setOrientation(LinearLayout.VERTICAL);
        root.addView(warnings);

        Button openSettings = button("Open date & time settings", v -> startActivity(Reminder.settingsIntent()));
        root.addView(openSettings);

        root.addView(header("Add clock change"));

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        dateButton = button("", v -> pickDate());
        timeButton = button("", v -> pickTime());
        row.addView(dateButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        row.addView(timeButton, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        root.addView(row);

        interpretHint = text(13, false);
        root.addView(interpretHint);

        TextView toLabel = text(14, false);
        toLabel.setText("Change clocks to:");
        toLabel.setPadding(0, dp(8), 0, 0);
        root.addView(toLabel);

        List<String> labels = new ArrayList<>();
        for (int h = Zones.MIN_HOURS; h <= Zones.MAX_HOURS; h++) {
            labels.add(Zones.utcLabel(h * 60) + "   (" + Zones.gmtLabel(h * 60) + ")");
        }
        offsetSpinner = new Spinner(this);
        ArrayAdapter<String> adapter = new ArrayAdapter<>(this, android.R.layout.simple_spinner_item, labels);
        adapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        offsetSpinner.setAdapter(adapter);
        root.addView(offsetSpinner);

        customZone = new EditText(this);
        customZone.setHint("Optional IANA zone, e.g. Asia/Dubai");
        customZone.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        customZone.setSingleLine(true);
        root.addView(customZone);

        root.addView(button("Add change", v -> addChange()));

        root.addView(header("Scheduled changes"));
        listView = new LinearLayout(this);
        listView.setOrientation(LinearLayout.VERTICAL);
        root.addView(listView);

        root.addView(button("Clear finished", v -> clearFinished()));

        ScrollView scroll = new ScrollView(this);
        scroll.setFitsSystemWindows(true);
        scroll.addView(root);
        setContentView(scroll);

        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(NOTIF_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{NOTIF_PERMISSION}, 1);
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        // The user may have changed the zone in Settings; clear reminders that now match.
        SystemReceiver.clearMatching(this, Zones.phoneZone());
        Scheduler.syncAll(this);
        selectDefaultOffset();
        refresh();
    }

    @Override
    public void onRequestPermissionsResult(int code, String[] perms, int[] results) {
        refresh();
    }

    private void selectDefaultOffset() {
        List<Change> list = Store.load(this);
        String zone = Zones.phoneZone();
        for (Change c : list) if (c.isActive()) zone = c.zone;
        int hours = Math.round(Zones.offsetMinutes(zone, System.currentTimeMillis()) / 60f) + 1;
        hours = Math.max(Zones.MIN_HOURS, Math.min(Zones.MAX_HOURS, hours));
        offsetSpinner.setSelection(hours - Zones.MIN_HOURS);
    }

    private void refresh() {
        long now = System.currentTimeMillis();
        String phone = Zones.phoneZone();
        status.setText("Phone now: " + Zones.describe(phone, now) + "\n"
                + ZonedDateTime.now().format(DATE) + "  " + ZonedDateTime.now().format(TIME));

        warnings.removeAllViews();
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(NOTIF_PERMISSION) != PackageManager.PERMISSION_GRANTED) {
            warnings.addView(button("⚠ Notifications are off - tap to allow", v -> openAppSettings()));
        }
        if (!Scheduler.canExact(this) && Build.VERSION.SDK_INT >= 31) {
            warnings.addView(button("⚠ Allow exact alarms", v -> startActivity(
                    new Intent(Settings.ACTION_REQUEST_SCHEDULE_EXACT_ALARM,
                            Uri.parse("package:" + getPackageName())))));
        }

        dateButton.setText(date.format(DATE));
        timeButton.setText(time.format(TIME));
        String from = interpretZone(Store.load(this), LocalDateTime.of(date, time));
        interpretHint.setText("Entered as ship's time " + Zones.describe(from, now));

        listView.removeAllViews();
        List<Change> list = Store.load(this);
        if (list.isEmpty()) {
            TextView empty = text(14, false);
            empty.setText("Nothing scheduled.");
            listView.addView(empty);
        }
        for (Change c : list) listView.addView(changeRow(c, now));
    }

    private View changeRow(Change c, long now) {
        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(0, dp(6), 0, dp(6));

        ZonedDateTime local = Instant.ofEpochMilli(c.time).atZone(ZoneId.of(c.from));
        StringBuilder sb = new StringBuilder();
        sb.append(local.format(DATE)).append(" ").append(local.format(TIME))
                .append(" (").append(Zones.utcLabel(Zones.offsetMinutes(c.from, c.time))).append(")")
                .append("\n→ ").append(Zones.describe(c.zone, c.time))
                .append("   ").append(c.state);
        if (Change.PENDING.equals(c.state) && c.time > now) sb.append(" · in ").append(until(c.time - now));
        TextView t = text(15, Change.REMINDED.equals(c.state));
        t.setText(sb);
        if (!c.isActive()) t.setAlpha(0.5f);
        row.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));

        if (Change.REMINDED.equals(c.state)) {
            row.addView(small("Done", v -> setState(c.id, Change.DONE)));
        }
        row.addView(small("✕", v -> confirmDelete(c)));
        return row;
    }

    private static String until(long ms) {
        long min = ms / 60000;
        long d = min / 1440, h = (min % 1440) / 60, m = min % 60;
        if (d > 0) return d + "d " + h + "h";
        if (h > 0) return h + "h " + m + "m";
        return m + "m";
    }

    /** Ship's time zone in force at the entered local time: that of the previous active change. */
    static String interpretZone(List<Change> list, LocalDateTime ldt) {
        String zone = Zones.phoneZone();
        for (int i = 0; i < 4; i++) {
            long t = ldt.atZone(ZoneId.of(zone)).toInstant().toEpochMilli();
            String z = Zones.phoneZone();
            for (Change c : list) if (c.isActive() && c.time < t) z = c.zone;
            if (z.equals(zone)) break;
            zone = z;
        }
        return zone;
    }

    private void addChange() {
        String zone;
        String custom = customZone.getText().toString().trim();
        if (!custom.isEmpty()) {
            if (!Zones.isValid(custom)) {
                Toast.makeText(this, "Unknown time zone: " + custom, Toast.LENGTH_LONG).show();
                return;
            }
            zone = custom;
        } else {
            zone = Zones.etcId(offsetSpinner.getSelectedItemPosition() + Zones.MIN_HOURS);
        }

        List<Change> list = Store.load(this);
        LocalDateTime ldt = LocalDateTime.of(date, time);
        String from = interpretZone(list, ldt);
        long t = ldt.atZone(ZoneId.of(from)).toInstant().toEpochMilli();
        if (t <= System.currentTimeMillis()) {
            Toast.makeText(this, "That time is in the past", Toast.LENGTH_LONG).show();
            return;
        }

        Change c = new Change();
        c.id = System.currentTimeMillis();
        c.time = t;
        c.zone = zone;
        c.from = from;
        list.add(c);
        Store.save(this, list);
        Scheduler.schedule(this, c);
        customZone.setText("");
        Toast.makeText(this, "Scheduled", Toast.LENGTH_SHORT).show();
        selectDefaultOffset();
        refresh();
    }

    private void setState(long id, String state) {
        List<Change> list = Store.load(this);
        Change c = Store.find(list, id);
        if (c == null) return;
        c.state = state;
        Scheduler.cancel(this, id);
        Reminder.cancel(this, c);
        Store.save(this, list);
        refresh();
    }

    private void confirmDelete(Change c) {
        new AlertDialog.Builder(this)
                .setMessage("Delete this change?")
                .setPositiveButton("Delete", (d, w) -> {
                    List<Change> list = Store.load(this);
                    list.removeIf(x -> x.id == c.id);
                    Scheduler.cancel(this, c.id);
                    Reminder.cancel(this, c);
                    Store.save(this, list);
                    refresh();
                })
                .setNegativeButton("Cancel", null)
                .show();
    }

    private void clearFinished() {
        List<Change> list = Store.load(this);
        list.removeIf(c -> !c.isActive());
        Store.save(this, list);
        refresh();
    }

    private void pickDate() {
        new DatePickerDialog(this, (v, y, m, d) -> {
            date = LocalDate.of(y, m + 1, d);
            refresh();
        }, date.getYear(), date.getMonthValue() - 1, date.getDayOfMonth()).show();
    }

    private void pickTime() {
        new TimePickerDialog(this, (v, h, m) -> {
            time = LocalTime.of(h, m);
            refresh();
        }, time.getHour(), time.getMinute(), true).show();
    }

    private void openAppSettings() {
        startActivity(new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS,
                Uri.parse("package:" + getPackageName())));
    }

    private TextView text(int sp, boolean bold) {
        TextView t = new TextView(this);
        t.setTextSize(sp);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        return t;
    }

    private TextView header(String s) {
        TextView t = text(16, true);
        t.setText(s);
        t.setPadding(0, dp(20), 0, dp(4));
        return t;
    }

    private Button button(String s, View.OnClickListener l) {
        Button b = new Button(this);
        b.setText(s);
        b.setAllCaps(false);
        b.setOnClickListener(l);
        return b;
    }

    private Button small(String s, View.OnClickListener l) {
        Button b = button(s, l);
        b.setMinWidth(0);
        b.setMinimumWidth(dp(48));
        return b;
    }

    private int dp(int v) {
        return Math.round(v * getResources().getDisplayMetrics().density);
    }
}
