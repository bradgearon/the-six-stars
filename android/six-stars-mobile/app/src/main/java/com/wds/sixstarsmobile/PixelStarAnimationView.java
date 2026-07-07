package com.wds.sixstarsmobile;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Movie;
import android.graphics.Paint;
import android.graphics.Rect;
import android.graphics.RectF;
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
 * The renderer trims transparent / near-black padding before drawing so full-body
 * sprites fill the stage instead of sitting tiny inside large source canvases.
 */
public final class PixelStarAnimationView extends View {
    private static final long FRAME_DURATION_MS = 150L;
    private static final int MAX_GIF_FRAMES_TO_SAMPLE = 18;
    private static final int MIN_GIF_FRAMES_TO_SAMPLE = 6;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Paint paint = new Paint();
    private Bitmap[] frames = new Bitmap[0];
    private int frameIndex;
    private String activeKey = "";

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
        if (key.equals(activeKey) && frames.length > 0) return;
        activeKey = key;

        handler.removeCallbacks(animator);
        frameIndex = 0;
        frames = new Bitmap[0];

        String[] candidates = new String[]{
                "star_animations/" + normalizedStar + "/" + normalizedState + ".gif",
                "star_animations/" + normalizedStar + "/idle.gif"
        };
        for (String path : candidates) {
            Bitmap[] gifFrames = loadGifAsCroppedFrames(path);
            if (gifFrames.length > 0) {
                setFrames(gifFrames);
                return;
            }
        }

        int[] conventionFrames = findConventionFrameResources(normalizedStar, normalizedState);
        if (conventionFrames.length == 0 && !"idle".equals(normalizedState)) {
            conventionFrames = findConventionFrameResources(normalizedStar, "idle");
        }
        setFrameResources(conventionFrames.length > 0 ? conventionFrames : fallbackResourceIds);
    }

    private Bitmap[] loadGifAsCroppedFrames(String assetPath) {
        try (InputStream in = getContext().getAssets().open(assetPath)) {
            Movie movie = Movie.decodeStream(in);
            if (movie == null || movie.width() <= 0 || movie.height() <= 0) return new Bitmap[0];
            int duration = movie.duration();
            if (duration <= 0) duration = 900;
            int count = Math.max(MIN_GIF_FRAMES_TO_SAMPLE, Math.min(MAX_GIF_FRAMES_TO_SAMPLE, duration / 120));
            Bitmap[] raw = new Bitmap[count];
            for (int i = 0; i < count; i++) {
                Bitmap bitmap = Bitmap.createBitmap(movie.width(), movie.height(), Bitmap.Config.ARGB_8888);
                Canvas c = new Canvas(bitmap);
                c.drawColor(Color.TRANSPARENT);
                movie.setTime((int) ((long) i * duration / count));
                movie.draw(c, 0, 0);
                raw[i] = bitmap;
            }
            return cropFramesToContent(raw);
        } catch (Exception ignored) {
            return new Bitmap[0];
        }
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
        handler.removeCallbacks(animator);
        if (resourceIds == null || resourceIds.length == 0) {
            frames = new Bitmap[0];
            frameIndex = 0;
            invalidate();
            return;
        }
        Bitmap[] next = new Bitmap[resourceIds.length];
        for (int i = 0; i < resourceIds.length; i++) {
            next[i] = BitmapFactory.decodeResource(getResources(), resourceIds[i]);
        }
        setFrames(cropFramesToContent(next));
    }

    private void setFrames(Bitmap[] next) {
        handler.removeCallbacks(animator);
        frames = next == null ? new Bitmap[0] : next;
        frameIndex = 0;
        invalidate();
        if (frames.length > 1) handler.postDelayed(animator, FRAME_DURATION_MS);
    }

    private Bitmap[] cropFramesToContent(Bitmap[] sourceFrames) {
        if (sourceFrames == null || sourceFrames.length == 0) return new Bitmap[0];
        Rect union = null;
        for (Bitmap frame : sourceFrames) {
            Rect content = findContentBounds(frame);
            if (content == null) continue;
            if (union == null) union = new Rect(content);
            else union.union(content);
        }
        if (union == null) return sourceFrames;
        union.left = Math.max(0, union.left - 3);
        union.top = Math.max(0, union.top - 3);
        int maxW = sourceFrames[0].getWidth();
        int maxH = sourceFrames[0].getHeight();
        union.right = Math.min(maxW, union.right + 3);
        union.bottom = Math.min(maxH, union.bottom + 3);
        if (union.width() <= 0 || union.height() <= 0) return sourceFrames;

        Bitmap[] cropped = new Bitmap[sourceFrames.length];
        for (int i = 0; i < sourceFrames.length; i++) {
            Bitmap frame = sourceFrames[i];
            if (frame == null || frame.getWidth() < union.right || frame.getHeight() < union.bottom) {
                cropped[i] = frame;
            } else {
                cropped[i] = Bitmap.createBitmap(frame, union.left, union.top, union.width(), union.height());
            }
        }
        return cropped;
    }

    private Rect findContentBounds(Bitmap bitmap) {
        if (bitmap == null || bitmap.getWidth() <= 0 || bitmap.getHeight() <= 0) return null;
        int w = bitmap.getWidth();
        int h = bitmap.getHeight();
        int left = w;
        int top = h;
        int right = -1;
        int bottom = -1;
        int[] row = new int[w];
        for (int y = 0; y < h; y++) {
            bitmap.getPixels(row, 0, w, 0, y, w, 1);
            for (int x = 0; x < w; x++) {
                if (!isVisibleContent(row[x])) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        if (right < left || bottom < top) return null;
        return new Rect(left, top, right + 1, bottom + 1);
    }

    private boolean isVisibleContent(int color) {
        int alpha = Color.alpha(color);
        if (alpha <= 18) return false;
        int max = Math.max(Color.red(color), Math.max(Color.green(color), Color.blue(color)));
        // Crops pure/near-black source canvases while preserving dark outfits and outlines.
        return max > 14;
    }

    @Override protected void onDetachedFromWindow() {
        handler.removeCallbacks(animator);
        super.onDetachedFromWindow();
    }

    @Override protected void onDraw(Canvas canvas) {
        super.onDraw(canvas);
        if (frames.length == 0 || frames[frameIndex] == null) return;
        Bitmap bitmap = frames[frameIndex];
        int vw = getWidth() - getPaddingLeft() - getPaddingRight();
        int vh = getHeight() - getPaddingTop() - getPaddingBottom();
        if (vw <= 0 || vh <= 0 || bitmap.getWidth() <= 0 || bitmap.getHeight() <= 0) return;

        float scale = Math.min((float) vw / bitmap.getWidth(), (float) vh / bitmap.getHeight()) * 0.96f;
        if (scale <= 0f) return;
        int drawW = Math.max(1, Math.round(bitmap.getWidth() * scale));
        int drawH = Math.max(1, Math.round(bitmap.getHeight() * scale));
        int left = getPaddingLeft() + (vw - drawW) / 2;
        int top = getPaddingTop() + (vh - drawH) / 2;
        RectF dst = new RectF(left, top, left + drawW, top + drawH);
        canvas.drawBitmap(bitmap, null, dst, paint);
    }
}
