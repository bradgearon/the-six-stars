package com.wds.sixstarsstandalone;

import java.util.ArrayList;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.HashSet;

/**
 * Emits TTS chunks while foreground Google streaming continues.
 *
 * The first complete sentence is released immediately for low latency. Later very short sentences
 * are coalesced into one/two-sentence chunks so Qwen is not asked to synthesize tiny fragments such
 * as "I do play." as separate network calls.
 */
final class SentenceChunker {
    private static final int TARGET_MIN_CHARS = 72;
    private static final int TARGET_MAX_CHARS = 230;
    private static final Set<String> ABBREVIATIONS = new HashSet<>();
    static {
        String[] values = {"mr.","mrs.","ms.","dr.","prof.","sr.","jr.","st.","mt.","vs.","etc.","e.g.","i.e.","a.m.","p.m."};
        for (String value : values) ABBREVIATIONS.add(value);
    }

    private final StringBuilder pending = new StringBuilder();
    private boolean firstChunkEmitted;

    synchronized List<String> accept(String delta) {
        pending.append(delta == null ? "" : delta);
        return drain(false);
    }

    synchronized List<String> finish() { return drain(true); }

    private List<String> drain(boolean force) {
        List<String> out = new ArrayList<>();
        while (true) {
            List<Integer> ends = completeSentenceEnds(pending.toString());
            if (ends.isEmpty()) break;

            if (!firstChunkEmitted) {
                int end = ends.get(0);
                emitPrefix(out, end);
                firstChunkEmitted = true;
                continue;
            }

            int firstEnd = ends.get(0);
            String first = pending.substring(0, firstEnd).trim();
            if (ends.size() >= 2) {
                int secondEnd = ends.get(1);
                String combined = pending.substring(0, secondEnd).trim();
                if (combined.length() <= TARGET_MAX_CHARS || first.length() < TARGET_MIN_CHARS) {
                    emitPrefix(out, secondEnd);
                    continue;
                }
            }

            if (first.length() >= TARGET_MIN_CHARS || force) {
                emitPrefix(out, firstEnd);
                continue;
            }
            break;
        }

        if (force) {
            String tail = pending.toString().trim();
            if (!tail.isEmpty()) out.add(tail);
            pending.setLength(0);
        }
        return out;
    }

    private void emitPrefix(List<String> out, int endExclusive) {
        String value = pending.substring(0, Math.min(endExclusive, pending.length())).trim();
        pending.delete(0, Math.min(endExclusive, pending.length()));
        trimLeadingWhitespace();
        if (!value.isEmpty()) out.add(value);
    }

    private void trimLeadingWhitespace() {
        while (pending.length() > 0 && Character.isWhitespace(pending.charAt(0))) pending.deleteCharAt(0);
    }

    private static List<Integer> completeSentenceEnds(String text) {
        List<Integer> ends = new ArrayList<>();
        if (text == null || text.isEmpty()) return ends;
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (c != '.' && c != '?' && c != '!') continue;

            // Treat ... as an expressive pause, not three sentence boundaries.
            if (c == '.' && ((i + 1 < text.length() && text.charAt(i + 1) == '.') || (i > 0 && text.charAt(i - 1) == '.'))) continue;
            if (c == '.' && isAbbreviation(text, i)) continue;
            if (c == '.' && i > 0 && i + 1 < text.length() && Character.isDigit(text.charAt(i - 1)) && Character.isDigit(text.charAt(i + 1))) continue;

            int end = i + 1;
            while (end < text.length() && isClosing(text.charAt(end))) end++;
            if (end == text.length() || Character.isWhitespace(text.charAt(end))) ends.add(end);
        }
        return ends;
    }

    private static boolean isAbbreviation(String text, int periodIndex) {
        int start = periodIndex;
        while (start > 0 && !Character.isWhitespace(text.charAt(start - 1))) start--;
        String token = text.substring(start, periodIndex + 1).toLowerCase(Locale.US);
        return ABBREVIATIONS.contains(token) || token.matches("(?:[a-z]\\.){2,}");
    }

    private static boolean isClosing(char c) {
        return c == '"'
                || c == '\''
                || c == '”'
                || c == '’'
                || c == ')'
                || c == ']'
                || c == '}';
    }
}
