package com.wds.sixstarsmobile;

import android.content.Context;
import android.os.SystemClock;

import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
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
                String primaryUrl = settings.preferredBridgeUrl();
                PreparedSpeechStore.SyncResult result;
                try {
                    result = PreparedSpeechStore.sync(context, primaryUrl, starId);
                } catch (Exception primaryError) {
                    String fallbackUrl = settings.bridgeUrl();
                    if (BridgeAddressService.normalizeBaseUrl(primaryUrl).equalsIgnoreCase(BridgeAddressService.normalizeBaseUrl(fallbackUrl))) {
                        throw primaryError;
                    }
                    result = PreparedSpeechStore.sync(context, fallbackUrl, starId);
                }
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

    void suppressIntent(String intentId) {
        if (intentId == null || intentId.trim().isEmpty()) return;
        PreparedSpeechStore.SyncResult current = latest;
        List<PreparedSpeechStore.CachedItem> remaining = new ArrayList<>();
        for (PreparedSpeechStore.CachedItem item : current.readyItems) {
            if (item == null || item.intentId.equalsIgnoreCase(intentId)) continue;
            remaining.add(item);
        }
        if (remaining.size() == current.readyItems.size()) return;

        double highestScore = 0.0;
        String highestPriority = "low";
        int highestRank = 0;
        for (PreparedSpeechStore.CachedItem item : remaining) {
            highestScore = Math.max(highestScore, item.effectiveScore);
            int rank = item.priorityRank();
            if (rank > highestRank) {
                highestRank = rank;
                highestPriority = item.priority;
            }
        }
        latest = new PreparedSpeechStore.SyncResult(
                remaining,
                current.preparingCount,
                highestScore,
                highestPriority,
                current.speakBeforeReplyEnabled,
                current.speakBeforeReplyProbability,
                current.highPrioritySpeakBeforeReplyProbability,
                current.speakBeforeReplyMaximumSeconds,
                current.minimumPageDwellSeconds,
                current.mediumPriorityPageDwellSeconds,
                current.girlfriendMediumSpeakProbability,
                current.highConnectionMediumSpeakProbability,
                current.highConnectionThreshold,
                current.minimumMinutesBetweenAutoSpeech,
                current.highPriorityAutoSpeakThreshold,
                current.mediumPriorityAutoSpeakThreshold);
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
