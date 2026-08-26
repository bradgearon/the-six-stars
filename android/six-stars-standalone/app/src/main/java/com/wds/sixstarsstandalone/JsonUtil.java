package com.wds.sixstarsstandalone;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.Iterator;
import java.util.regex.Pattern;

final class JsonUtil {
    private static final Pattern TRAILING_COMMA = Pattern.compile(",\\s*([}\\]])");
    private JsonUtil() {}

    static String readUtf8(File file) throws IOException {
        try (InputStream in = new FileInputStream(file)) { return readUtf8(in); }
    }

    static String readUtf8(InputStream in) throws IOException {
        ByteArrayOutputStream out = new ByteArrayOutputStream();
        byte[] buf = new byte[8192];
        int n;
        while ((n = in.read(buf)) >= 0) out.write(buf, 0, n);
        return out.toString(StandardCharsets.UTF_8.name());
    }

    static JSONObject readObject(File file) throws IOException, JSONException {
        return new JSONObject(normalizeJson(readUtf8(file)));
    }

    static JSONObject parseObject(String text) throws JSONException {
        String normalized = normalizeJson(text == null ? "{}" : text.trim());
        int start = normalized.indexOf('{');
        int end = normalized.lastIndexOf('}');
        if (start >= 0 && end > start) normalized = normalized.substring(start, end + 1);
        return new JSONObject(normalized);
    }

    static String normalizeJson(String text) {
        if (text == null) return "{}";
        String current = text.replace("\\ufeff", "");
        for (int i = 0; i < 8; i++) {
            String next = TRAILING_COMMA.matcher(current).replaceAll("$1");
            if (next.equals(current)) break;
            current = next;
        }
        return current;
    }

    static void writeObject(File file, JSONObject value) throws IOException {
        File parent = file.getParentFile();
        if (parent != null && !parent.exists() && !parent.mkdirs()) throw new IOException("Unable to create " + parent);
        File tmp = new File(file.getAbsolutePath() + ".tmp");
        try (Writer writer = new OutputStreamWriter(new FileOutputStream(tmp), StandardCharsets.UTF_8)) {
            writer.write(value.toString(2));
        }
        if (file.exists() && !file.delete()) throw new IOException("Unable to replace " + file);
        if (!tmp.renameTo(file)) throw new IOException("Unable to move " + tmp + " to " + file);
    }

    static JSONObject deepCopy(JSONObject obj) throws JSONException { return new JSONObject(obj.toString()); }
    static JSONArray array(JSONObject o, String key) { JSONArray a = o.optJSONArray(key); return a == null ? new JSONArray() : a; }
    static JSONObject object(JSONObject o, String key) { JSONObject x = o.optJSONObject(key); return x == null ? new JSONObject() : x; }
    static String string(JSONObject o, String key) { return o.optString(key, ""); }

    static void mergeObject(JSONObject target, JSONObject source) throws JSONException {
        Iterator<String> keys = source.keys();
        while (keys.hasNext()) {
            String key = keys.next();
            target.put(key, source.get(key));
        }
    }
}
