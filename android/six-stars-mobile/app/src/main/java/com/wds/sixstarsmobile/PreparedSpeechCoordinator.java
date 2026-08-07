package com.wds.sixstarsmobile;

import android.content.Context;
import android.os.SystemClock;

import java.util.Collections;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executor;

/** Owns prepared-speech reconciliation, eligibility, and cooldown policy. */
final class PreparedSpeechCoordinator {
    private final Context context;
    private final MobileSettingsService settings;
    private final Executor executor;
    private volatile PreparedSpeechStore.SyncResult latest = new PreparedSpeechStore.SyncResult(Collections.emptyList(), 0, 0.0, "low");
    private volatile boolean syncing;
    private volatile long lastSyncElapsedMs;
    private volatile long blockedUntilElapsedMs;
    private volatile long lastAutoSpeakElapsedMs;

    PreparedSpeechCoordinator(Context context, MobileSettingsService settings, Executor executor) {
        this.context = context.getApplicationContext();
        this.settings = settings;
        this.executor = executor;
    }

    CompletableFuture<PreparedSpeechStore.SyncResult> sync(String starId, boolean force) {
        long now = SystemClock.elapsedRealtime();
        if (!force && (syncing || now - lastSyncElapsedMs < 2200L)) return CompletableFuture.completedFuture(latest);
        syncing = true;
        return CompletableFuture.supplyAsync(() -> {
            try {
                PreparedSpeechStore.SyncResult result = PreparedSpeechStore.sync(context, settings.bridgeUrl(), starId);
                latest = result;
                lastSyncElapsedMs = SystemClock.elapsedRealtime();
                return result;
            } catch (Exception error) {
                throw new RuntimeException(error);
            } finally {
                syncing = false;
            }
        }, executor);
    }

    PreparedSpeechStore.SyncResult latest() { return latest; }
    PreparedSpeechStore.CachedItem bestFor(String starId) {
        for (PreparedSpeechStore.CachedItem item : latest.readyItems) if (item.starId.equalsIgnoreCase(starId)) return item;
        return null;
    }
    void blockFor(long delayMs) {
        blockedUntilElapsedMs = Math.max(blockedUntilElapsedMs, SystemClock.elapsedRealtime() + Math.max(0L, delayMs));
        settings.setPreparedListenNotBeforeMs(System.currentTimeMillis() + Math.max(0L, delayMs));
    }
    void allowAfter(long delayMs) {
        blockedUntilElapsedMs = SystemClock.elapsedRealtime() + Math.max(0L, delayMs);
        settings.setPreparedListenNotBeforeMs(System.currentTimeMillis() + Math.max(0L, delayMs));
    }
    boolean isBlocked() { return SystemClock.elapsedRealtime() < blockedUntilElapsedMs; }
    boolean shouldAutoSpeak(PreparedSpeechStore.CachedItem item, long selectedVisibleSinceMs, boolean girlfriend, double connectionPercent) {
        if (item == null || isBlocked()) return false;
        long now = SystemClock.elapsedRealtime();
        long minimumInterval = latest.minimumMinutesBetweenAutoSpeech * 60_000L;
        if (now - lastAutoSpeakElapsedMs < minimumInterval) return false;
        long dwell = now - selectedVisibleSinceMs;
        long requiredDwell = ("medium".equals(item.priority) ? latest.mediumPriorityPageDwellSeconds : latest.minimumPageDwellSeconds) * 1000L;
        double threshold = "medium".equals(item.priority) ? latest.mediumPriorityAutoSpeakThreshold : latest.highPriorityAutoSpeakThreshold;
        return dwell >= requiredDwell && item.effectiveScore >= threshold;
    }
    void markAutoSpoken() { lastAutoSpeakElapsedMs = SystemClock.elapsedRealtime(); }
}
