package com.wds.sixstarsmobile;

import android.content.Context;
import android.content.Intent;

/** Owns background initiative service scheduling and prefetched initiative persistence. */
final class StarInitiativeCoordinator {
    private final Context context;
    private final MobileSettingsService settings;

    StarInitiativeCoordinator(Context context, MobileSettingsService settings) {
        this.context = context.getApplicationContext();
        this.settings = settings;
    }

    void setEnabled(boolean enabled) {
        settings.setStarInitiativeEnabled(enabled);
        if (enabled) start("initiative enabled", true); else stop("initiative disabled");
    }

    void start(String reason, boolean checkSoon) {
        Intent intent = new Intent(context, StarInitiativeBackgroundService.class);
        intent.setAction(checkSoon ? StarInitiativeBackgroundService.ACTION_CHECK_NOW : StarInitiativeBackgroundService.ACTION_START);
        intent.putExtra("reason", reason == null ? "" : reason);
        context.startService(intent);
    }

    void stop(String reason) {
        Intent intent = new Intent(context, StarInitiativeBackgroundService.class);
        intent.setAction(StarInitiativeBackgroundService.ACTION_STOP);
        intent.putExtra("reason", reason == null ? "" : reason);
        context.startService(intent);
    }

    PrefetchedInitiativeStore.CachedInitiative loadPrefetched(String sessionId, String starId) {
        return PrefetchedInitiativeStore.load(context, sessionId, starId);
    }
    void clearPrefetched() { PrefetchedInitiativeStore.clear(context); }
    boolean isEnabled() { return settings.isStarInitiativeEnabled(); }
}
