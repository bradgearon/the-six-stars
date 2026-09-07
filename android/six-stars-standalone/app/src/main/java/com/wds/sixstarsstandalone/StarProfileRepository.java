package com.wds.sixstarsstandalone;

import org.json.JSONObject;

import java.io.File;
import java.util.*;

final class StarProfileRepository {
    static final String[] IDS = {"Elusiaa", "Kaelia", "Maera", "Khavia", "Auerlis", "Nyxari"};
    private final Map<String, JSONObject> profiles = new LinkedHashMap<>();

    StarProfileRepository(ContentRepository content) throws Exception {
        for (String id : IDS) {
            File file = content.file("Stars/" + id.toLowerCase(Locale.ROOT) + ".json");
            profiles.put(id.toLowerCase(Locale.ROOT), JsonUtil.readObject(file));
        }
    }

    JSONObject get(String starId) {
        JSONObject p = profiles.get((starId == null ? "" : starId).toLowerCase(Locale.ROOT));
        if (p == null) throw new IllegalArgumentException("Unknown StarId " + starId);
        return p;
    }

    List<JSONObject> all() { return new ArrayList<>(profiles.values()); }
    String displayName(String id) { return get(id).optString("displayName", id); }
}
