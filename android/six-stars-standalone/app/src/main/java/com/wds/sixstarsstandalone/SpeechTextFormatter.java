package com.wds.sixstarsstandalone;

import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * Cleans foreground Star speech for display and TTS.
 *
 * The Star is already the speaker, so model-generated literary dialogue wrappers
 * such as "I do play." or “I do play.” are unnecessary.
 *
 * Action beats inside *asterisks* are preserved.
 * Ordinary quoted words such as "Void" are preserved.
 */
final class SpeechTextFormatter {

    private static final Pattern QUOTED_SPOKEN_SENTENCE = Pattern.compile(
            "[\"“]([^\"”]+?[.!?…])[\"”](?=\\s|$)",
            Pattern.DOTALL);

    private static final Pattern WHITESPACE = Pattern.compile("\\s+");

    private SpeechTextFormatter() {
    }

    static String normalize(String value) {
        if (value == null || value.isEmpty()) {
            return "";
        }

        String text = value
                .replace('\r', ' ')
                .replace('\n', ' ')
                .trim();

        // Remove literary dialogue wrappers around complete sentences.
        // Example:
        //   “I do play.”
        // becomes:
        //   I do play.
        //
        // But:
        //   the word "Void"
        // remains unchanged.
        for (int pass = 0; pass < 4; pass++) {
            Matcher matcher = QUOTED_SPOKEN_SENTENCE.matcher(text);
            if (!matcher.find()) {
                break;
            }

            text = matcher.replaceAll("$1");
        }

        // If the entire returned chunk itself is wrapped in quotation marks,
        // remove only that outer pair.
        if (text.length() >= 2) {
            char first = text.charAt(0);
            char last = text.charAt(text.length() - 1);

            boolean straightQuotes = first == '"' && last == '"';
            boolean curlyQuotes = first == '“' && last == '”';

            if (straightQuotes || curlyQuotes) {
                text = text.substring(1, text.length() - 1).trim();
            }
        }

        return WHITESPACE.matcher(text)
                .replaceAll(" ")
                .trim();
    }

    static String normalizeChunk(String value) {
        return normalize(value);
    }
}