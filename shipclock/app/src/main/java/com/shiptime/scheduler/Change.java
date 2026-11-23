package com.shiptime.scheduler;

import org.json.JSONException;
import org.json.JSONObject;

/** One scheduled clock change. */
final class Change {
    static final String PENDING = "PENDING";
    static final String REMINDED = "REMINDED";
    static final String DONE = "DONE";
    static final String SKIPPED = "SKIPPED";

    long id;
    /** Instant of the change, epoch millis (UTC). */
    long time;
    /** Zone to change to, e.g. "Etc/GMT-4" (= UTC+4) or an IANA ID. */
    String zone;
    /** Zone the entered date/time was interpreted in (ship's time before the change). */
    String from;
    String state = PENDING;

    boolean isActive() {
        return PENDING.equals(state) || REMINDED.equals(state);
    }

    int notificationId() {
        return Long.hashCode(id);
    }

    JSONObject toJson() throws JSONException {
        JSONObject o = new JSONObject();
        o.put("id", id);
        o.put("time", time);
        o.put("zone", zone);
        o.put("from", from);
        o.put("state", state);
        return o;
    }

    static Change fromJson(JSONObject o) {
        Change c = new Change();
        c.id = o.optLong("id");
        c.time = o.optLong("time");
        c.zone = o.optString("zone", "Etc/GMT");
        c.from = o.optString("from", c.zone);
        c.state = o.optString("state", PENDING);
        return c;
    }
}
