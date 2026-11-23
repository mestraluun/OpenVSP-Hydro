package com.shiptime.scheduler;

import java.time.ZoneId;
import java.util.Locale;
import java.util.TimeZone;

/** Whole-hour Etc/GMT zones and labels. Note Etc/GMT-4 means UTC+4 (inverted sign). */
final class Zones {
    static final int MIN_HOURS = -12;
    static final int MAX_HOURS = 14;

    private Zones() {}

    static String etcId(int hours) {
        if (hours == 0) return "Etc/GMT";
        return hours > 0 ? "Etc/GMT-" + hours : "Etc/GMT+" + (-hours);
    }

    static boolean isValid(String id) {
        try {
            ZoneId.of(id);
            return true;
        } catch (Exception e) {
            return false;
        }
    }

    static boolean isEtc(String id) {
        return id.equals("Etc/GMT") || id.startsWith("Etc/GMT+") || id.startsWith("Etc/GMT-")
                || id.equals("UTC") || id.equals("Etc/UTC");
    }

    static int offsetMinutes(String id, long at) {
        return TimeZone.getTimeZone(id).getOffset(at) / 60000;
    }

    /** "UTC+4", "UTC-3", "UTC", "UTC+5:30". */
    static String utcLabel(int minutes) {
        if (minutes == 0) return "UTC";
        String sign = minutes > 0 ? "+" : "-";
        int m = Math.abs(minutes);
        if (m % 60 == 0) return "UTC" + sign + (m / 60);
        return String.format(Locale.US, "UTC%s%d:%02d", sign, m / 60, m % 60);
    }

    /** "GMT+04:00", as listed in Samsung's Time zone > UTC offset screen. */
    static String gmtLabel(int minutes) {
        String sign = minutes < 0 ? "-" : "+";
        int m = Math.abs(minutes);
        return String.format(Locale.US, "GMT%s%02d:%02d", sign, m / 60, m % 60);
    }

    /** "UTC+4" for Etc zones, "Asia/Dubai (UTC+4)" for IANA zones. */
    static String describe(String id, long at) {
        String utc = utcLabel(offsetMinutes(id, at));
        return isEtc(id) ? utc : id + " (" + utc + ")";
    }

    /** True if a phone set to {@code phoneZone} shows the same clock as {@code target} now. */
    static boolean matches(String phoneZone, String target, long now) {
        return offsetMinutes(phoneZone, now) == offsetMinutes(target, now);
    }

    static String phoneZone() {
        return TimeZone.getDefault().getID();
    }
}
