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
import android.graphics.drawable.Drawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.util.AttributeSet;
import android.view.View;

import java.io.InputStream;
import java.util.ArrayList;
import java.util.Locale;

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
    private static final float SPRITE_VIEWPORT_FRACTION = 0.70f;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Paint paint = new Paint();
    private Bitmap[] frames = new Bitmap[0];
    private int frameIndex;
    private String activeKey = "";
    private Drawable activeDrawable;

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
        paint.setFilterBitmap(false);
        paint.setDither(false);
        setWillNotDraw(false);
    }

    public void setStarAnimation(String starId, String state, int[] fallbackResourceIds) {
        String normalizedStar = normalizeAssetPart(starId);
        String normalizedState = normalizeAssetPart(state == null || state.length() == 0 ? "idle" : state);
        String key = normalizedStar + ":" + normalizedState;
        if (key.equals(activeKey) && (activeDrawable != null || frames.length > 0)) return;
        activeKey = key;

        clearCurrentAnimation();

        String[] candidates = new String[]{
                "star_animations/" + normalizedStar + "/" + normalizedState + ".gif",
                "star_animations/" + normalizedStar + "/idle.gif"
        };
        for (String path : candidates) {
            if (loadGif(path)) return;
        }

        int[] conventionFrames = findConventionFrameResources(normalizedStar, normalizedState);
        if (conventionFrames.length == 0 && !"idle".equals(normalizedState)) {
            conventionFrames = findConventionFrameResources(normalizedStar, "idle");
        }
        setFrameResources(conventionFrames.length > 0 ? conventionFrames : fallbackResourceIds);
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
        try {
            ImageDecoder.Source source = ImageDecoder.createSource(getContext().getAssets(), assetPath);
            Drawable drawable = ImageDecoder.decodeDrawable(source);
            if (drawable.getIntrinsicWidth() <= 0 || drawable.getIntrinsicHeight() <= 0) return false;
            setDrawable(drawable);
            return true;
        } catch (Exception ignored) {
            return false;
        }
    }

    private void setDrawable(Drawable drawable) {
        clearCurrentAnimation();
        activeDrawable = drawable;
        activeDrawable.setCallback(this);
        if (activeDrawable instanceof Animatable && isAttachedToWindow()) {
            ((Animatable) activeDrawable).start();
        }
        invalidate();
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
        handler.removeCallbacks(animator);
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
        float targetWidth = availableWidth * SPRITE_VIEWPORT_FRACTION;
        float targetHeight = availableHeight * SPRITE_VIEWPORT_FRACTION;
        float scale = Math.min(targetWidth / safeWidth, targetHeight / safeHeight);
        int drawWidth = Math.max(1, Math.round(safeWidth * scale));
        int drawHeight = Math.max(1, Math.round(safeHeight * scale));
        int left = getPaddingLeft() + (availableWidth - drawWidth) / 2;
        int top = getPaddingTop() + (availableHeight - drawHeight) / 2;
        return new Rect(left, top, left + drawWidth, top + drawHeight);
    }
}
