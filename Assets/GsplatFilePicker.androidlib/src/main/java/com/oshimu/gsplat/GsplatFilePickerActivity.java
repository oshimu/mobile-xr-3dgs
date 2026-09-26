package com.oshimu.gsplat;

import android.app.Activity;
import android.content.ActivityNotFoundException;
import android.content.Intent;
import android.database.Cursor;
import android.net.Uri;
import android.os.Bundle;
import android.os.ParcelFileDescriptor;
import android.provider.OpenableColumns;

import java.io.InputStream;

/**
 * Transparent, non-exported activity that wraps Android's Storage Access
 * Framework document picker (ACTION_OPEN_DOCUMENT). Confirmed on a Quest 3
 * device to resolve to com.android.documentsui.picker.PickActivity and to
 * render and respond to input inside the headset.
 *
 * Unity's C# side (GsplatLod.SplatFilePicker) starts this activity via
 * startPick() and then polls the static fields below on every frame --
 * there is no UnitySendMessage callback here, because whether this
 * activity's result reaches Unity should not depend on which Unity scene
 * happens to be active when the user finishes picking (or backs out).
 *
 * All static fields are written only from this activity's own main-thread
 * callbacks (onCreate / onActivityResult) and read from Unity's main thread
 * (via AndroidJavaClass.GetStatic); they are volatile so a poll can never
 * observe a torn/partial update, and there is otherwise no concurrent
 * writer, so no further synchronization is needed.
 */
public class GsplatFilePickerActivity extends Activity {

    public static final int STATE_IDLE = 0;
    public static final int STATE_RUNNING = 1;
    public static final int STATE_SUCCESS = 2;
    public static final int STATE_CANCELLED = 3;
    public static final int STATE_ERROR = 4;

    private static final int REQUEST_CODE_OPEN_DOCUMENT = 4201;
    /** Leading bytes read for headerBytes -- ample for any real splat PLY header. */
    private static final int HEADER_PREFIX_BYTES = 64 * 1024;

    public static volatile int state = STATE_IDLE;
    /** POSIX fd from openFileDescriptor(...).detachFd() -- see onActivityResult. */
    public static volatile int fd = -1;
    public static volatile String displayName = "";
    public static volatile long size = -1;
    public static volatile String error = "";
    /**
     * Leading bytes of the picked file's content, read via a *separate*
     * openInputStream() -- independent of the fd above, so reading it never
     * moves that fd's position. Empty (never null) if the read failed or
     * hasn't happened yet; a failure here is not fatal to the pick (the C#
     * side falls back to a file-size-based estimate).
     */
    public static volatile byte[] headerBytes = new byte[0];

    /** Resets all result fields to STATE_IDLE. Safe to call at any time. */
    public static void reset() {
        state = STATE_IDLE;
        fd = -1;
        displayName = "";
        size = -1;
        error = "";
        headerBytes = new byte[0];
    }

    /**
     * Starts this activity from Unity's current activity. This activity's
     * own onCreate() immediately launches the actual document picker.
     */
    public static void startPick(Activity unityActivity) {
        reset();
        state = STATE_RUNNING;
        unityActivity.startActivity(new Intent(unityActivity, GsplatFilePickerActivity.class));
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);

        Intent openDocument = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        openDocument.addCategory(Intent.CATEGORY_OPENABLE);
        openDocument.setType("*/*");

        // No ActivityOptions.setLaunchBounds() here: on a Quest 3 the picker
        // opens as its own window (taskAffinity=com.android.documentsui), and
        // Horizon OS -- not the Android task bounds -- decides that panel's
        // size. Measured 2026-09-16: it always opens at 500x800 px
        // (400dp x 640dp, near Horizon's 384dp x 500dp minimum) where Meta's
        // own panel apps get 1280x800 (Settings) or 1840x1000 (Library);
        // setLaunchBounds was ignored, "am task resize" changed the reported
        // bounds without changing what is drawn, and a size the user drags to
        // is not remembered for the next launch. The manifest <layout
        // android:defaultWidth/> lever documented at
        // developers.meta.com/horizon/essentials/horizon-os-panel-sizing/
        // only applies to an app's own activities, not to the system picker.
        // The user can widen the panel by hand; nothing here can.
        try {
            startActivityForResult(openDocument, REQUEST_CODE_OPEN_DOCUMENT);
        } catch (ActivityNotFoundException e) {
            state = STATE_ERROR;
            error = "no activity found for ACTION_OPEN_DOCUMENT: " + e.getMessage();
            finish();
        }
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);

        if (requestCode != REQUEST_CODE_OPEN_DOCUMENT) {
            finish();
            return;
        }

        if (resultCode != RESULT_OK || data == null || data.getData() == null) {
            state = STATE_CANCELLED;
            finish();
            return;
        }

        Uri uri = data.getData();
        try {
            queryMetadata(uri);
            headerBytes = readHeaderPrefix(uri);

            ParcelFileDescriptor pfd = getContentResolver().openFileDescriptor(uri, "r");
            if (pfd == null) {
                state = STATE_ERROR;
                error = "openFileDescriptor returned null for " + uri;
                finish();
                return;
            }
            // detachFd(): ownership of the underlying POSIX fd moves to
            // whoever reads it (Unity's C# wrapper, then the Rust
            // pipeline) -- this Java side must not close pfd/the fd itself
            // after this point.
            fd = pfd.detachFd();
            state = STATE_SUCCESS;
        } catch (Exception e) {
            state = STATE_ERROR;
            error = "failed to open picked document: " + e.getMessage();
        }

        finish();
    }

    /**
     * Reads up to HEADER_PREFIX_BYTES from the start of the picked document
     * via a fresh openInputStream() -- deliberately NOT the fd that will be
     * detach()ed for the convert pipeline below, so this read can never
     * perturb that fd's read position. Best-effort: any failure yields an
     * empty array rather than failing the pick.
     */
    private byte[] readHeaderPrefix(Uri uri) {
        try (InputStream in_ = getContentResolver().openInputStream(uri)) {
            if (in_ == null) return new byte[0];
            byte[] buf = new byte[HEADER_PREFIX_BYTES];
            int total = 0;
            while (total < buf.length) {
                int read = in_.read(buf, total, buf.length - total);
                if (read < 0) break;
                total += read;
            }
            if (total == buf.length) return buf;
            byte[] trimmed = new byte[total];
            System.arraycopy(buf, 0, trimmed, 0, total);
            return trimmed;
        } catch (Exception e) {
            return new byte[0];
        }
    }

    /** Fills in displayName/size from the document's OpenableColumns, best-effort. */
    private void queryMetadata(Uri uri) {
        displayName = "";
        size = -1;
        String[] projection = { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE };
        try (Cursor cursor = getContentResolver().query(uri, projection, null, null, null)) {
            if (cursor != null && cursor.moveToFirst()) {
                int nameIdx = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME);
                if (nameIdx >= 0 && !cursor.isNull(nameIdx)) {
                    displayName = cursor.getString(nameIdx);
                }
                int sizeIdx = cursor.getColumnIndex(OpenableColumns.SIZE);
                if (sizeIdx >= 0 && !cursor.isNull(sizeIdx)) {
                    size = cursor.getLong(sizeIdx);
                }
            }
        }
        if (displayName == null || displayName.isEmpty()) {
            // Fall back to the URI's last path segment so format detection
            // (which keys off the extension in the display name) still has
            // something to work with even if the provider didn't return one.
            String last = uri.getLastPathSegment();
            displayName = last != null ? last : "";
        }
    }
}
