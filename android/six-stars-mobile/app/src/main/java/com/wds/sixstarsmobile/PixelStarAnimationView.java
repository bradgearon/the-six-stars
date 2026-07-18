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
        setWillNotDraw(false);
    }

    public void setStarAnimation(String starId, String state, int[] fallbackResourceIds) {
        String normalizedStar = normalizeAssetPart(starId);
        String normalizedState = normalizeAssetPart(state == null || state.length() == 0 ? "idle" : state);
        String key = normalizedStar + ":" + normalizedState;
        if (key.equals(activeKey) && (activeDrawable != null || frames.length > 0)) {
            preloadStarGifs(normalizedStar);
            return;
        }
        activeKey = key;

        clearCurrentAnimation();

        String[] candidates = new String[]{
                "star_animations/" + normalizedStar + "/" + normalizedState + ".gif",
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
            int[] conventionFrames = findConventionFrameResources(normalizedStar, normalizedState);
            if (conventionFrames.length == 0 && !"idle".equals(normalizedState)) {
                conventionFrames = findConventionFrameResources(normalizedStar, "idle");
            }
            setFrameResources(conventionFrames.length > 0 ? conventionFrames : fallbackResourceIds);
        }

        // Decode the currently selected Star's idle and talking GIFs while the
        // user is looking at the screen. Switching to talking then reuses the
        // same decoded drawable instead of reopening the asset at speech start.
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
        preloadAnimatedGif("star_animations/" + normalizedStar + "/shirt-off.gif");
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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P && activeDrawable instanceof AnimatedImageDrawable) {
            ((AnimatedImageDrawable) activeDrawable).setRepeatCount(AnimatedImageDrawable.REPEAT_INFINITE);
        }
        if (activeDrawable instanceof Animatable && isAttachedToWindow()) {
            ((Animatable) activeDrawable).start();
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
        String normalizedState = normalizeAssetPart(state);
        String assetPath = "star_animations/" + normalizedStar + "/" + normalizedState + ".gif";
        activeKey = normalizedStar + ":" + normalizedState + ":one-shot";

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            return playOneShotAnimatedGif(assetPath, Math.max(0L, holdLastFrameMs), onFinished);
        }

        // API 26-27 cannot animate GIFs with ImageDecoder. Preserve the optional-asset
        // contract by showing its first frame for the requested hold duration.
        try (InputStream in = getContext().getAssets().open(assetPath)) {
            Bitmap firstFrame = BitmapFactory.decodeStream(in);
            if (firstFrame == null) return false;
            setFrames(new Bitmap[]{firstFrame});
            final int generation = animationGeneration;
            handler.postDelayed(() -> {
                if (generation == animationGeneration && onFinished != null) onFinished.run();
            }, Math.max(1L, holdLastFrameMs));
            return true;
        } catch (Exception ignored) {
            return false;
        }
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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P
                && activeDrawable instanceof Animatable2
                && activeAnimationCallback != null) {
            ((Animatable2) activeDrawable).unregisterAnimationCallback(activeAnimationCallback);
        }
        activeAnimationCallback = null;
        if (activeDrawable instanceof Animatable) {
            ((Animatable) activeDrawable).stop();
        }
        if (activeDrawable != null) activeDrawable.setCallback(null);
        activeDrawable = null;
        frames = new Bitmap[0];
        frameIndex = 0;
    }

    @Override protected boolean verifyDrawable(Drawable who) {
        return who == activeDrawable || super.verifyDrawable(who);
    }

    @Override protected void onAttachedToWindow() {
        super.onAttachedToWindow();
        if (activeDrawable instanceof Animatable) {
            ((Animatable) activeDrawable).start();
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
        super.onDetachedFromWindow();
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        int vw = getWidth() - getPaddingLeft() - getPaddingRight();
        int vh = getHeight() - getPaddingTop() - getPaddingBottom();
        if (vw <= 0 || vh <= 0) return;

        if (activeDrawable != null) {
            Rect destination = destinationRect(
                    activeDrawable.getIntrinsicWidth(),
                    activeDrawable.getIntrinsicHeight(),
                    vw,
                    vh);
            activeDrawable.setBounds(destination);
            activeDrawable.draw(canvas);
            return;
        }

        if (frames.length == 0 || frames[frameIndex] == null) return;
        Bitmap bitmap = frames[frameIndex];
        Rect destination = destinationRect(bitmap.getWidth(), bitmap.getHeight(), vw, vh);
        canvas.drawBitmap(bitmap, null, new RectF(destination), paint);
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
