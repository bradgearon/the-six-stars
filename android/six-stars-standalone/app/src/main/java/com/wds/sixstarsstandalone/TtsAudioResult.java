package com.wds.sixstarsstandalone;

/** Provider-neutral synthesized audio returned to the Android playback layer. */
final class TtsAudioResult {
    final byte[] bytes;
    final String mime;

    TtsAudioResult(byte[] bytes, String mime) {
        this.bytes = bytes == null ? new byte[0] : bytes;
        this.mime = mime == null || mime.trim().isEmpty() ? "audio/wav" : mime;
    }
}
