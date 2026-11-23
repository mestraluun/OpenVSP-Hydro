package com.shiptime.scheduler;

import android.content.Context;
import android.content.SharedPreferences;

import org.json.JSONArray;
import org.json.JSONException;

import java.util.ArrayList;
import java.util.Comparator;
import java.util.List;

/** Changes stored as a JSON array in SharedPreferences, sorted by time. */
final class Store {
    private static final String PREFS = "shipclock";
    private static final String KEY = "changes";

    private Store() {}

    static synchronized List<Change> load(Context ctx) {
        List<Change> list = new ArrayList<>();
        String json = prefs(ctx).getString(KEY, "[]");
        try {
            JSONArray a = new JSONArray(json);
            for (int i = 0; i < a.length(); i++) {
                list.add(Change.fromJson(a.getJSONObject(i)));
            }
        } catch (JSONException e) {
            // Corrupt data: start empty rather than crash.
        }
        list.sort(Comparator.comparingLong(c -> c.time));
        return list;
    }

    static synchronized void save(Context ctx, List<Change> list) {
        list.sort(Comparator.comparingLong(c -> c.time));
        JSONArray a = new JSONArray();
        try {
            for (Change c : list) a.put(c.toJson());
        } catch (JSONException e) {
            return;
        }
        prefs(ctx).edit().putString(KEY, a.toString()).commit();
    }

    static Change find(List<Change> list, long id) {
        for (Change c : list) if (c.id == id) return c;
        return null;
    }

    private static SharedPreferences prefs(Context ctx) {
        return ctx.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }
}
