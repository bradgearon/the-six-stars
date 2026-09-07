package com.wds.sixstarsstandalone;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.*;

/**
 * Small Android-side renderer for the subset of Scriban used by the runtime prompt templates:
 * variables, if/else-if/else/end, for item in list, equality/inequality, numeric comparisons, and
 * .size. Keeping the original .sbn files means prompt wording remains the desktop source of truth.
 */
final class SbnTemplateRenderer {
    String render(String template, JSONObject data) {
        Map<String, Object> scope = new HashMap<>();
        scope.put("data", data);
        return renderRange(template == null ? "" : template, 0, template == null ? 0 : template.length(), scope);
    }

    private String renderRange(String t, int start, int end, Map<String, Object> scope) {
        StringBuilder out = new StringBuilder();
        int cursor = start;
        while (cursor < end) {
            int open = t.indexOf("{{", cursor);
            if (open < 0 || open >= end) { out.append(t, cursor, end); break; }
            out.append(t, cursor, open);
            int close = t.indexOf("}}", open + 2);
            if (close < 0 || close >= end) { out.append(t, open, end); break; }
            String tag = t.substring(open + 2, close).trim();

            if (tag.startsWith("if ")) {
                Block block = findBlock(t, close + 2, end);
                out.append(renderIf(t, tag.substring(3).trim(), close + 2, block, scope));
                cursor = block.endClose;
                continue;
            }
            if (tag.startsWith("for ")) {
                Block block = findBlock(t, close + 2, end);
                String spec = tag.substring(4).trim();
                int in = spec.indexOf(" in ");
                if (in > 0) {
                    String name = spec.substring(0, in).trim();
                    Object list = resolve(spec.substring(in + 4).trim(), scope);
                    for (Object item : iterable(list)) {
                        Map<String,Object> child = new HashMap<>(scope); child.put(name, item);
                        out.append(renderRange(t, close + 2, block.endOpen, child));
                    }
                }
                cursor = block.endClose;
                continue;
            }
            if (tag.equals("else") || tag.startsWith("else if ") || tag.equals("end")) {
                // These are consumed by the enclosing block. If malformed, suppress instead of leaking syntax.
                cursor = close + 2;
                continue;
            }

            Object value = resolve(tag, scope);
            if (value != null && value != JSONObject.NULL) out.append(stringify(value));
            cursor = close + 2;
        }
        return out.toString();
    }

    private String renderIf(String t, String firstCondition, int bodyStart, Block block, Map<String,Object> scope) {
        List<Branch> branches = new ArrayList<>();
        String condition = firstCondition;
        int segmentStart = bodyStart;
        for (Marker marker : block.markers) {
            branches.add(new Branch(condition, segmentStart, marker.open));
            condition = marker.tag.equals("else") ? null : marker.tag.substring("else if ".length()).trim();
            segmentStart = marker.close;
        }
        branches.add(new Branch(condition, segmentStart, block.endOpen));
        for (Branch branch : branches) {
            if (branch.condition == null || eval(branch.condition, scope)) return renderRange(t, branch.start, branch.end, scope);
        }
        return "";
    }

    private Block findBlock(String t, int from, int limit) {
        int depth = 1;
        List<Marker> markers = new ArrayList<>();
        int cursor = from;
        while (cursor < limit) {
            int open = t.indexOf("{{", cursor); if (open < 0 || open >= limit) break;
            int close = t.indexOf("}}", open + 2); if (close < 0 || close >= limit) break;
            String tag = t.substring(open + 2, close).trim();
            if (tag.startsWith("if ") || tag.startsWith("for ")) depth++;
            else if (tag.equals("end")) {
                depth--;
                if (depth == 0) return new Block(open, close + 2, markers);
            } else if (depth == 1 && (tag.equals("else") || tag.startsWith("else if "))) {
                markers.add(new Marker(tag, open, close + 2));
            }
            cursor = close + 2;
        }
        return new Block(limit, limit, markers);
    }

    private boolean eval(String expression, Map<String,Object> scope) {
        String e = expression.trim();
        for (String op : new String[]{" == ", " != ", " >= ", " <= ", " > ", " < "}) {
            int p = e.indexOf(op);
            if (p > 0) {
                Object left = resolve(e.substring(0,p).trim(), scope);
                Object right = literalOrResolve(e.substring(p + op.length()).trim(), scope);
                int cmp = compare(left, right);
                switch (op.trim()) {
                    case "==": return equalsValue(left, right);
                    case "!=": return !equalsValue(left, right);
                    case ">=": return cmp >= 0;
                    case "<=": return cmp <= 0;
                    case ">": return cmp > 0;
                    case "<": return cmp < 0;
                }
            }
        }
        return truthy(resolve(e, scope));
    }

    private Object literalOrResolve(String s, Map<String,Object> scope) {
        if (s.equals("true")) return true;
        if (s.equals("false")) return false;
        if (s.equals("null")) return null;
        if ((s.startsWith("\"") && s.endsWith("\"")) || (s.startsWith("'") && s.endsWith("'"))) return s.substring(1, s.length()-1);
        try { return Double.parseDouble(s); } catch (Exception ignored) {}
        return resolve(s, scope);
    }

    private Object resolve(String path, Map<String,Object> scope) {
        if (path == null || path.isEmpty()) return "";
        String[] parts = path.split("\\.");
        Object current = scope.get(parts[0]);
        if (current == null && parts.length == 1) return literalOrBare(path, scope);
        for (int i = 1; i < parts.length; i++) {
            String part = parts[i];
            if (part.equals("size")) { current = sizeOf(current); continue; }
            current = getProperty(current, part);
            if (current == null) break;
        }
        return current;
    }

    private Object literalOrBare(String path, Map<String,Object> scope) {
        if (scope.containsKey(path)) return scope.get(path);
        if (path.equals("true")) return true; if (path.equals("false")) return false;
        return "";
    }

    private Object getProperty(Object current, String key) {
        if (current instanceof JSONObject) return ((JSONObject) current).opt(key);
        if (current instanceof Map) return ((Map<?,?>) current).get(key);
        return null;
    }

    private static int sizeOf(Object value) {
        if (value instanceof JSONArray) return ((JSONArray)value).length();
        if (value instanceof Collection) return ((Collection<?>)value).size();
        if (value instanceof String) return ((String)value).length();
        return 0;
    }

    private static Iterable<Object> iterable(Object value) {
        List<Object> out = new ArrayList<>();
        if (value instanceof JSONArray) {
            JSONArray a = (JSONArray)value; for (int i=0;i<a.length();i++) out.add(a.opt(i));
        } else if (value instanceof Iterable) for (Object x : (Iterable<?>)value) out.add(x);
        return out;
    }

    private static boolean truthy(Object o) {
        if (o == null || o == JSONObject.NULL) return false;
        if (o instanceof Boolean) return (Boolean)o;
        if (o instanceof Number) return ((Number)o).doubleValue() != 0;
        if (o instanceof String) return !((String)o).isEmpty();
        return sizeOf(o) > 0;
    }

    private static boolean equalsValue(Object a, Object b) {
        if (a == JSONObject.NULL) a = null; if (b == JSONObject.NULL) b = null;
        if (a == null || b == null) return a == b;
        if (a instanceof Number && b instanceof Number) return Double.compare(((Number)a).doubleValue(), ((Number)b).doubleValue()) == 0;
        return String.valueOf(a).equals(String.valueOf(b));
    }

    private static int compare(Object a, Object b) {
        if (a instanceof Number && b instanceof Number) return Double.compare(((Number)a).doubleValue(), ((Number)b).doubleValue());
        return String.valueOf(a == null ? "" : a).compareTo(String.valueOf(b == null ? "" : b));
    }

    private static String stringify(Object value) {
        if (value instanceof JSONObject || value instanceof JSONArray) return value.toString();
        return String.valueOf(value);
    }

    private static final class Block { final int endOpen,endClose; final List<Marker> markers; Block(int a,int b,List<Marker> m){endOpen=a;endClose=b;markers=m;} }
    private static final class Marker { final String tag; final int open,close; Marker(String t,int o,int c){tag=t;open=o;close=c;} }
    private static final class Branch { final String condition; final int start,end; Branch(String c,int s,int e){condition=c;start=s;end=e;} }
}
