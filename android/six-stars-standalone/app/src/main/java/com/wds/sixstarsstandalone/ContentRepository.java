package com.wds.sixstarsstandalone;

import android.content.Context;
import android.content.res.AssetManager;

import java.io.*;

/**
 * Materializes the bundled desktop Content tree into app-private storage. Runtime code always reads
 * through this local content root, so a later editor/settings/import layer has a normal filesystem
 * reference instead of being coupled to Android AssetManager semantics.
 */
final class ContentRepository {
    private static final String ASSET_ROOT = "sixstars_content";
    private final Context context;
    private final File root;

    ContentRepository(Context context) {
        this.context = context.getApplicationContext();
        this.root = new File(this.context.getFilesDir(), "content");
    }

    synchronized void ensureMaterialized() throws IOException {
        File marker = new File(root, ".desktop-content-snapshot");
        if (marker.exists()) return;
        if (root.exists()) deleteRecursively(root);
        if (!root.mkdirs()) throw new IOException("Unable to create local content folder " + root);
        copyAssetTree(ASSET_ROOT, root);
        try (Writer w = new FileWriter(marker)) { w.write("Six Stars desktop Content snapshot\n"); }
    }

    File root() { return root; }
    File file(String relativePath) { return new File(root, relativePath.replace('/', File.separatorChar)); }
    String readText(String relativePath) throws IOException { return JsonUtil.readUtf8(file(relativePath)); }

    private void copyAssetTree(String assetPath, File destination) throws IOException {
        AssetManager assets = context.getAssets();
        String[] children = assets.list(assetPath);
        if (children == null || children.length == 0) {
            File parent = destination.getParentFile();
            if (parent != null && !parent.exists()) parent.mkdirs();
            try (InputStream in = assets.open(assetPath); OutputStream out = new FileOutputStream(destination)) {
                byte[] buffer = new byte[8192];
                int n;
                while ((n = in.read(buffer)) >= 0) out.write(buffer, 0, n);
            }
            return;
        }
        if (!destination.exists() && !destination.mkdirs()) throw new IOException("Unable to create " + destination);
        for (String child : children) copyAssetTree(assetPath + "/" + child, new File(destination, child));
    }

    private static void deleteRecursively(File file) {
        File[] children = file.listFiles();
        if (children != null) for (File child : children) deleteRecursively(child);
        //noinspection ResultOfMethodCallIgnored
        file.delete();
    }
}
