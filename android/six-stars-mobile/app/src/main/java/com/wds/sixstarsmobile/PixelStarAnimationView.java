package com.wds.sixstarsmobile;

import android.annotation.TargetApi;
import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.ImageDecoder;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.drawable.Animatable;
import android.graphics.drawable.Animatable2;
import android.graphics.drawable.AnimatedImageDrawable;
import android.graphics.drawable.Drawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.AttributeSet;
import android.view.View;

import java.io.InputStream;
import java.util.ArrayList;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.concurrent.ConcurrentHashMap;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Pixel-art Star renderer used by the Android production companion screen.
 *
 * Layout stays declarative: put this View in XML, then call setStarAnimation(...)
 * from the Activity when the selected Star or animation state changes.
 *
 * Asset convention:
 *   app/src/main/assets/star_animations/elusiaa/idle.gif
 *   app/src/main/assets/star_animations/elusiaa/talking.gif
 *   app/src/main/assets/star_animations/elusiaa/happy.gif
 *
 * Fallback convention:
 *   res/drawable-nodpi/star_elusiaa_idle_0.png
 *   res/drawable-nodpi/star_elusiaa_idle_1.png
 *
 * Every state is drawn into the same centered viewport. The GIF source canvas is
 * intentionally preserved instead of content-cropping each state independently,
 * so idle.gif and talking.gif cannot resize the character and make her jump.
 */
public final class PixelStarAnimationView extends View {
    private static final long FRAME_DURATION_MS = 150L;
    private static final ExecutorService GIF_PRELOAD_EXECUTOR =
            Executors.newSingleThreadExecutor(runnable -> {
                Thread thread = new Thread(runnable, "six-stars-gif-preload");
                thread.setDaemon(true);
                return thread;
            });

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Paint paint = new Paint();
    private final Map<String, Drawable> gifCache = new ConcurrentHashMap<>();
    private final Set<String> gifPreloadsInFlight = ConcurrentHashMap.newKeySet();
    private final Set<String> missingGifAssets = ConcurrentHashMap.newKeySet();
    private StarLipSyncConfig lipSyncConfig;
    private Drawable lipTalkingDrawable;
    private String lipTalkingAssetPath = "";
    private boolean lipTalkingActive;
    private String activeBaseStar = "";
    private String activeBaseState = "";
    private long baseAnimationStartedAtMs;
    private Bitmap[] frames = new Bitmap[0];
    private int frameIndex;
    private String activeKey = "";
    private Drawable activeDrawable;
    private Animatable2.AnimationCallback activeAnimationCallback;
    private int animationGeneration;

    private final Runnable animator = new Runnable() {
        @Override public void run() {
            if (frames.length > 1) {
                frameIndex = (frameIndex + 1) % frames.length;
                invalidate();
                handler.postDelayed(this, FRAME_DURATION_MS);
            }
        }
    };

    public PixelStarAnimationView(Context context) {
        super(context);
        init();
    }

    public PixelStarAnimationView(Context context, AttributeSet attrs) {
        super(context, attrs);
        init();
    }

    public PixelStarAnimationView(Context context, AttributeSet attrs, int defStyleAttr) {
        super(context, attrs, defStyleAttr);
        init();
    }

    private void init() {
        paint.setAntiAlias(false);
        paint.setFilterBitmap(false);
        paint.setDither(false);
        lipSyncConfig = StarLipSyncConfig.load(getContext());
        setWillNotDraw(false);
    }

    public void setStarAnimation(String starId, String state, int[] fallbackResourceIds) {
        String normalizedStar = normalizeAssetPart(starId);
        String normalizedState = normalizeAssetPart(state == null || state.length() == 0 ? "idle" : state);

        // When lip anchors + lips-talking.gif are available, speech is composited over
        // idle.gif instead of swapping the entire character to talking.gif. That keeps
        // the body on the exact same idle frame/timeline while only the mouth animates.
        boolean useLipComposite = "talking".equals(normalizedState) && canUseLipTalking(normalizedStar);
        String baseState = useLipComposite ? "idle" : normalizedState;
        String key = normalizedStar + ":" + baseState;
        boolean sameBase = key.equals(activeKey) && (activeDrawable != null || frames.length > 0);

        if (!sameBase) {
            activeKey = key;
            clearCurrentAnimation();

            String[] candidates = new String[]{
                    "star_animations/" + normalizedStar + "/" + baseState + ".gif",
                    "star_animations/" + normalizedStar + "/idle.gif"
            };
            boolean loadedGif = false;
            for (String path : candidates) {
                if (loadGif(path)) {
                    loadedGif = true;
                    break;
                }
            }

            if (!loadedGif) {
                int[] conventionFrames = findConventionFrameResources(normalizedStar, baseState);
                if (conventionFrames.length == 0 && !"idle".equals(baseState)) {
                    conventionFrames = findConventionFrameResources(normalizedStar, "idle");
                }
                setFrameResources(conventionFrames.length > 0 ? conventionFrames : fallbackResourceIds);
            }
            activeBaseStar = normalizedStar;
            activeBaseState = baseState;
        }

        if (useLipComposite) {
            startLipTalking(normalizedStar);
        } else {
            stopLipTalking();
        }

        // Preload the selected Star's base states and the optional cropped lip GIF.
        preloadStarGifs(normalizedStar);
    }

    private boolean loadGif(String assetPath) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            return loadAnimatedGif(assetPath);
        }

        // ImageDecoder/AnimatedImageDrawable starts at API 28. On API 26-27,
        // preserve compatibility without the deprecated Movie API by showing
        // the GIF's first frame.
        try (InputStream in = getContext().getAssets().open(assetPath)) {
            Bitmap firstFrame = BitmapFactory.decodeStream(in);
            if (firstFrame == null) return false;
            setFrames(new Bitmap[]{firstFrame});
            return true;
        } catch (Exception ignored) {
            return false;
        }
    }

    @TargetApi(Build.VERSION_CODES.P)
    private boolean loadAnimatedGif(String assetPath) {
        if (missingGifAssets.contains(assetPath)) return false;

        Drawable drawable = gifCache.get(assetPath);
        if (drawable == null) {
            drawable = decodeAnimatedGif(assetPath);
            if (drawable == null) {
                missingGifAssets.add(assetPath);
                return false;
            }

            Drawable cached = gifCache.putIfAbsent(assetPath, drawable);
            if (cached != null) drawable = cached;
        }

        setDrawable(drawable);
        return true;
    }

    @TargetApi(Build.VERSION_CODES.P)
    private Drawable decodeAnimatedGif(String assetPath) {
        try {
            ImageDecoder.Source source = ImageDecoder.createSource(getContext().getAssets(), assetPath);
            Drawable drawable = ImageDecoder.decodeDrawable(source);
            if (drawable.getIntrinsicWidth() <= 0 || drawable.getIntrinsicHeight() <= 0) return null;

            // AnimatedImageDrawable enables bilinear filtering by default when
            // scaled. That makes a GIF look softer than the PNG fallback even
            // when both source canvases have exactly the same dimensions.
            drawable.setFilterBitmap(false);
            return drawable;
        } catch (Exception ignored) {
            return null;
        }
    }

    private void preloadStarGifs(String normalizedStar) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) return;
        preloadAnimatedGif("star_animations/" + normalizedStar + "/idle.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/talking.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/lips-talking.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/happy.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/confused.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/sad.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/angry.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/love.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/shirt-off.gif");
        preloadAnimatedGif("star_animations/" + normalizedStar + "/shirt_off.gif");
    }

    private boolean canUseLipTalking(String normalizedStar) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P || lipSyncConfig == null) return false;
        StarLipSyncConfig.AnimationPositions positions = lipSyncConfig.animation(normalizedStar, "idle");
        if (positions == null || !positions.hasConfiguredPosition) return false;
        return getOrDecodeGif("star_animations/" + normalizedStar + "/lips-talking.gif") != null;
    }

    @TargetApi(Build.VERSION_CODES.P)
    private Drawable getOrDecodeGif(String assetPath) {
        if (missingGifAssets.contains(assetPath)) return null;
        Drawable drawable = gifCache.get(assetPath);
        if (drawable != null) return drawable;
        drawable = decodeAnimatedGif(assetPath);
        if (drawable == null) {
            missingGifAssets.add(assetPath);
            return null;
        }
        Drawable cached = gifCache.putIfAbsent(assetPath, drawable);
        return cached == null ? drawable : cached;
    }

    private void startLipTalking(String normalizedStar) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.P) return;
        String assetPath = "star_animations/" + normalizedStar + "/lips-talking.gif";
        Drawable drawable = getOrDecodeGif(assetPath);
        if (drawable == null) return;
        if (lipTalkingActive && drawable == lipTalkingDrawable && assetPath.equals(lipTalkingAssetPath)) return;

        stopLipTalking();
        lipTalkingDrawable = drawable;
        lipTalkingAssetPath = assetPath;
        lipTalkingActive = true;
        lipTalkingDrawable.setFilterBitmap(false);
        lipTalkingDrawable.setCallback(this);
        lipTalkingDrawable.setVisible(true, true);
        if (lipTalkingDrawable instanceof AnimatedImageDrawable) {
            ((AnimatedImageDrawable) lipTalkingDrawable).setRepeatCount(AnimatedImageDrawable.REPEAT_INFINITE);
        }
        if (lipTalkingDrawable instanceof Animatable && isAttachedToWindow()) {
            ((Animatable) lipTalkingDrawable).start();
        }
        invalidate();
    }

    private void stopLipTalking() {
        lipTalkingActive = false;
        lipTalkingAssetPath = "";
        if (lipTalkingDrawable instanceof Animatable) {
            ((Animatable) lipTalkingDrawable).stop();
        }
        if (lipTalkingDrawable != null) {
            lipTalkingDrawable.setVisible(false, false);
            lipTalkingDrawable.setCallback(null);
        }
        lipTalkingDrawable = null;
        invalidate();
    }

    @TargetApi(Build.VERSION_CODES.P)
    private void preloadAnimatedGif(String assetPath) {
        if (gifCache.containsKey(assetPath)
                || missingGifAssets.contains(assetPath)
                || !gifPreloadsInFlight.add(assetPath)) return;

        GIF_PRELOAD_EXECUTOR.execute(() -> {
            try {
                Drawable drawable = decodeAnimatedGif(assetPath);
                if (drawable != null) {
                    gifCache.putIfAbsent(assetPath, drawable);
                } else {
                    missingGifAssets.add(assetPath);
                }
            } finally {
                gifPreloadsInFlight.remove(assetPath);
            }
        });
    }

    private void setDrawable(Drawable drawable) {
        clearCurrentAnimation();
        activeDrawable = drawable;
        activeDrawable.setFilterBitmap(false);
        activeDrawable.setCallback(this);
        activeDrawable.setVisible(true, true);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && activeDrawable instanceof AnimatedImageDrawable) {
            ((AnimatedImageDrawable) activeDrawable).setRepeatCount(AnimatedImageDrawable.REPEAT_INFINITE);
        }
        if (activeDrawable instanceof Animatable && isAttachedToWindow()) {
            baseAnimationStartedAtMs = SystemClock.uptimeMillis();
            ((Animatable) activeDrawable).start();
        } else {
            baseAnimationStartedAtMs = SystemClock.uptimeMillis();
        }
        invalidate();
    }

    /**
     * Plays exactly one optional GIF state, holds its final frame, then invokes onFinished.
     * Unlike setStarAnimation, this never falls back to idle when the requested asset is absent.
     */
    public boolean playOneShotAnimation(
            String starId,
            String state,
            long holdLastFrameMs,
            Runnable onFinished) {
        String normalizedStar = normalizeAssetPart(starId);
        String fileState = normalizeAssetFilePart(state);
        String legacyState = normalizeAssetPart(state);
        String[] assetPaths = fileState.equals(legacyState)
                ? new String[]{"star_animations/" + normalizedStar + "/" + fileState + ".gif"}
                : new String[]{
                        "star_animations/" + normalizedStar + "/" + fileState + ".gif",
                        "star_animations/" + normalizedStar + "/" + legacyState + ".gif"
                };
        activeKey = normalizedStar + ":" + fileState + ":one-shot";

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            for (String assetPath : assetPaths) {
                if (playOneShotAnimatedGif(assetPath, Math.max(0L, holdLastFrameMs), onFinished)) {
                    return true;
                }
            }
            return false;
        }

        // API 26-27 cannot animate GIFs with ImageDecoder. Preserve the optional-asset
        // contract by showing its first frame for the requested hold duration.
        for (String assetPath : assetPaths) {
            try (InputStream in = getContext().getAssets().open(assetPath)) {
                Bitmap firstFrame = BitmapFactory.decodeStream(in);
                if (firstFrame == null) continue;
                setFrames(new Bitmap[]{firstFrame});
                final int generation = animationGeneration;
                handler.postDelayed(() -> {
                    if (generation == animationGeneration && onFinished != null) onFinished.run();
                }, Math.max(1L, holdLastFrameMs));
                return true;
            } catch (Exception ignored) {
                // Try the compatibility filename before reporting the optional animation missing.
            }
        }
        return false;
    }

    @TargetApi(Build.VERSION_CODES.P)
    private boolean playOneShotAnimatedGif(String assetPath, long holdLastFrameMs, Runnable onFinished) {
        if (missingGifAssets.contains(assetPath)) return false;

        Drawable drawable = gifCache.get(assetPath);
        if (drawable == null) {
            drawable = decodeAnimatedGif(assetPath);
            if (drawable == null) {
                missingGifAssets.add(assetPath);
                return false;
            }
            Drawable cached = gifCache.putIfAbsent(assetPath, drawable);
            if (cached != null) drawable = cached;
        }
        if (!(drawable instanceof AnimatedImageDrawable)) return false;

        clearCurrentAnimation();
        final AnimatedImageDrawable animated = (AnimatedImageDrawable) drawable;
        final int generation = animationGeneration;
        activeDrawable = animated;
        activeDrawable.setFilterBitmap(false);
        activeDrawable.setCallback(this);
        animated.setRepeatCount(0);

        activeAnimationCallback = new Animatable2.AnimationCallback() {
            @Override public void onAnimationEnd(Drawable endedDrawable) {
                handler.post(() -> {
                    if (generation != animationGeneration || activeDrawable != endedDrawable) return;
                    handler.postDelayed(() -> {
                        if (generation == animationGeneration
                                && activeDrawable == endedDrawable
                                && onFinished != null) {
                            onFinished.run();
                        }
                    }, holdLastFrameMs);
                });
            }
        };
        animated.registerAnimationCallback(activeAnimationCallback);
        if (isAttachedToWindow()) animated.start();
        invalidate();
        return true;
    }

    private int[] findConventionFrameResources(String star, String state) {
        ArrayList<Integer> ids = new ArrayList<>();
        for (int i = 0; i < 96; i++) {
            int resId = getResources().getIdentifier("star_" + star + "_" + state + "_" + i,
                    "drawable", getContext().getPackageName());
            if (resId == 0) break;
            ids.add(resId);
        }
        int[] values = new int[ids.size()];
        for (int i = 0; i < ids.size(); i++) values[i] = ids.get(i);
        return values;
    }

    private String normalizeAssetPart(String value) {
        if (value == null) return "idle";
        return value.trim().toLowerCase(Locale.US).replaceAll("[^a-z0-9_]+", "_");
    }

    private String normalizeAssetFilePart(String value) {
        if (value == null) return "idle";
        return value.trim().toLowerCase(Locale.US).replaceAll("[^a-z0-9_-]+", "_");
    }

    public void setFrameResources(int[] resourceIds) {
        clearCurrentAnimation();
        if (resourceIds == null || resourceIds.length == 0) {
            invalidate();
            return;
        }
        Bitmap[] next = new Bitmap[resourceIds.length];
        for (int i = 0; i < resourceIds.length; i++) {
            next[i] = BitmapFactory.decodeResource(getResources(), resourceIds[i]);
        }
        setFrames(next);
    }

    private void setFrames(Bitmap[] next) {
        clearCurrentAnimation();
        frames = next == null ? new Bitmap[0] : next;
        frameIndex = 0;
        invalidate();
        if (frames.length > 1) handler.postDelayed(animator, FRAME_DURATION_MS);
    }

    private void clearCurrentAnimation() {
        animationGeneration++;
        handler.removeCallbacks(animator);
        stopLipTalking();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                && activeDrawable instanceof Animatable2
                && activeAnimationCallback != null) {
            ((Animatable2) activeDrawable).unregisterAnimationCallback(activeAnimationCallback);
        }
        activeAnimationCallback = null;
        if (activeDrawable instanceof Animatable) {
            ((Animatable) activeDrawable).stop();
        }
        if (activeDrawable != null) {
            activeDrawable.setVisible(false, false);
            activeDrawable.setCallback(null);
        }
        activeDrawable = null;
        frames = new Bitmap[0];
        frameIndex = 0;
        activeBaseStar = "";
        activeBaseState = "";
        baseAnimationStartedAtMs = 0L;
    }

    @Override protected boolean verifyDrawable(Drawable who) {
        return who == activeDrawable || who == lipTalkingDrawable || super.verifyDrawable(who);
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (activeDrawable instanceof Animatable) {
            baseAnimationStartedAtMs = SystemClock.uptimeMillis();
            activeDrawable.setVisible(true, true);
            ((Animatable) activeDrawable).start();
        }
        if (lipTalkingDrawable instanceof Animatable && lipTalkingActive) {
            lipTalkingDrawable.setVisible(true, true);
            ((Animatable) lipTalkingDrawable).start();
        }
        if (activeDrawable == null && frames.length > 1) {
            handler.removeCallbacks(animator);
            handler.postDelayed(animator, FRAME_DURATION_MS);
        }
    }

    @Override protected void onDetachedFromWindow() {
        handler.removeCallbacks(animator);
        if (activeDrawable instanceof Animatable) {
            ((Animatable) activeDrawable).stop();
        }
        if (lipTalkingDrawable instanceof Animatable) {
            ((Animatable) lipTalkingDrawable).stop();
        }
        super.onDetachedFromWindow();
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int vw = getWidth() - getPaddingLeft() - getPaddingRight();
        int vh = getHeight() - getPaddingTop() - getPaddingBottom();
        if (vw <= 0 || vh <= 0) return;

        Rect baseDestination = null;
        int sourceWidth = 0;
        int sourceHeight = 0;

        if (activeDrawable != null) {
            sourceWidth = activeDrawable.getIntrinsicWidth();
            sourceHeight = activeDrawable.getIntrinsicHeight();
            baseDestination = destinationRect(sourceWidth, sourceHeight, vw, vh);
            activeDrawable.setBounds(baseDestination);
            activeDrawable.draw(canvas);
        } else if (frames.length > 0 && frames[frameIndex] != null) {
            Bitmap bitmap = frames[frameIndex];
            sourceWidth = bitmap.getWidth();
            sourceHeight = bitmap.getHeight();
            baseDestination = destinationRect(sourceWidth, sourceHeight, vw, vh);
            canvas.drawBitmap(bitmap, null, new RectF(baseDestination), paint);
        }

        if (baseDestination != null) {
            drawTalkingLips(canvas, baseDestination, sourceWidth, sourceHeight);
        }
    }

    private void drawTalkingLips(Canvas canvas, Rect baseDestination, int sourceWidth, int sourceHeight) {
        if (!lipTalkingActive || lipTalkingDrawable == null || lipSyncConfig == null
                || !"idle".equals(activeBaseState)) return;

        StarLipSyncConfig.AnimationPositions positions = lipSyncConfig.animation(activeBaseStar, activeBaseState);
        if (positions == null || !positions.hasConfiguredPosition) return;

        long elapsedMs = baseAnimationStartedAtMs <= 0L
                ? 0L
                : Math.max(0L, SystemClock.uptimeMillis() - baseAnimationStartedAtMs);
        StarLipSyncConfig.LipPosition position = positions.positionForElapsed(elapsedMs);
        if (position == null) return;

        float scaleX = baseDestination.width() / (float) Math.max(1, positions.sourceWidth);
        float scaleY = baseDestination.height() / (float) Math.max(1, positions.sourceHeight);
        int lipWidth = Math.max(1, Math.round(lipTalkingDrawable.getIntrinsicWidth() * scaleX));
        int lipHeight = Math.max(1, Math.round(lipTalkingDrawable.getIntrinsicHeight() * scaleY));
        int left = baseDestination.left + Math.round(position.x * scaleX);
        int top = baseDestination.top + Math.round(position.y * scaleY);
        lipTalkingDrawable.setBounds(left, top, left + lipWidth, top + lipHeight);
        lipTalkingDrawable.draw(canvas);
    }

    private Rect destinationRect(int sourceWidth, int sourceHeight, int availableWidth, int availableHeight) {
        int safeWidth = Math.max(1, sourceWidth);
        int safeHeight = Math.max(1, sourceHeight);
        float maxFitScale = Math.min(
                availableWidth / (float) safeWidth,
                availableHeight / (float) safeHeight);

        // The production Star GIFs share a 128x128 source canvas. Fill the
        // largest possible centered square and prefer a whole-number scale so
        // every source pixel maps to an exact block of screen pixels. This
        // makes the character substantially larger without reintroducing blur
        // or a size jump between idle.gif and talking.gif.
        float scale = maxFitScale >= 1.0f
                ? Math.max(1.0f, (float) Math.floor(maxFitScale))
                : maxFitScale;
        int drawWidth = Math.max(1, Math.round(safeWidth * scale));
        int drawHeight = Math.max(1, Math.round(safeHeight * scale));
        int left = getPaddingLeft() + (availableWidth - drawWidth) / 2;
        int top = getPaddingTop() + (availableHeight - drawHeight) / 2;
        return new Rect(left, top, left + drawWidth, top + drawHeight);
    }
}
