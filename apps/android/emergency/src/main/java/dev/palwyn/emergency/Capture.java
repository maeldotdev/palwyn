package dev.palwyn.emergency;

import android.graphics.Bitmap;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.hardware.display.VirtualDisplay;
import android.media.Image;
import android.media.ImageReader;
import android.os.IBinder;
import android.os.Looper;

import java.io.BufferedOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.DataOutputStream;
import java.io.IOException;
import java.io.OutputStream;
import java.nio.ByteBuffer;

import dev.palwyn.emergency.scrcpy.DisplayManager;
import dev.palwyn.emergency.scrcpy.SurfaceControl;

/**
 * Mirrors the main display into an ImageReader and writes Palwyn's screen frames: a big-endian u32 length, then a JPEG
 * (quality 60, long side at most 1280 px, at most 1 MiB). The display only produces a buffer when its content changes,
 * and a blocking write means a slow reader gets the newest frame next, not a backlog. Rebuilt on rotation or resize.
 */
final class Capture implements Runnable {
    private static final int MAX_SIDE = 1280;
    private static final int QUALITY = 60;
    private static final int MAX_FRAME = 1 << 20;

    private final DataOutputStream out;
    private final ByteArrayOutputStream jpeg = new ByteArrayOutputStream(256 * 1024);

    private ImageReader reader;
    private VirtualDisplay virtualDisplay;
    private IBinder display;
    private DisplayManager.DisplayInfo shown;
    private int width, height;

    Capture(OutputStream out) {
        this.out = new DataOutputStream(new BufferedOutputStream(out, 1 << 16));
    }

    @Override
    public void run() {
        Looper.prepare();
        try {
            loop();
        } catch (IOException e) {
            // the PC closed the connection
        } catch (Exception e) {
            System.err.println("Capture failed: " + e);
        } finally {
            release();
            System.exit(0);
        }
    }

    private void loop() throws Exception {
        long checked = 0;
        while (true) {
            long now = System.currentTimeMillis();
            if (now - checked >= 500) {
                checked = now;
                DisplayManager.DisplayInfo info = DisplayManager.get().getDisplayInfo(0);
                if (shown == null || info.width != shown.width || info.height != shown.height || info.rotation != shown.rotation) {
                    release();
                    start(info);
                }
            }
            Image image = reader.acquireLatestImage();
            if (image == null) {
                Thread.sleep(16);
                continue;
            }
            send(image);
        }
    }

    private void start(DisplayManager.DisplayInfo info) throws Exception {
        float scale = Math.min(1f, (float) MAX_SIDE / Math.max(info.width, info.height));
        width = Math.round(info.width * scale) & ~1;
        height = Math.round(info.height * scale) & ~1;
        reader = ImageReader.newInstance(width, height, PixelFormat.RGBA_8888, 2);
        try {
            virtualDisplay = DisplayManager.get().createVirtualDisplay("palwyn-emergency", width, height, 0, reader.getSurface());
        } catch (Exception displayManagerException) {
            // Android 13 and older: the shell can't mirror through DisplayManager, use SurfaceControl (as scrcpy does)
            display = SurfaceControl.createDisplay("palwyn-emergency");
            SurfaceControl.openTransaction();
            try {
                SurfaceControl.setDisplaySurface(display, reader.getSurface());
                SurfaceControl.setDisplayProjection(display, 0, new Rect(0, 0, info.width, info.height), new Rect(0, 0, width, height));
                SurfaceControl.setDisplayLayerStack(display, info.layerStack);
            } finally {
                SurfaceControl.closeTransaction();
            }
        }
        shown = info;
    }

    private void send(Image image) throws IOException {
        Image.Plane plane = image.getPlanes()[0];
        ByteBuffer buffer = plane.getBuffer();
        int stride = plane.getRowStride() / plane.getPixelStride();
        Bitmap full = Bitmap.createBitmap(stride, height, Bitmap.Config.ARGB_8888);
        full.copyPixelsFromBuffer(buffer);
        image.close();
        Bitmap frame = stride == width ? full : Bitmap.createBitmap(full, 0, 0, width, height);
        jpeg.reset();
        frame.compress(Bitmap.CompressFormat.JPEG, QUALITY, jpeg);
        if (frame != full) frame.recycle();
        full.recycle();
        if (jpeg.size() > MAX_FRAME) return;
        out.writeInt(jpeg.size());
        jpeg.writeTo(out);
        out.flush();
    }

    private void release() {
        if (virtualDisplay != null) {
            virtualDisplay.release();
            virtualDisplay = null;
        }
        if (display != null) {
            SurfaceControl.destroyDisplay(display);
            display = null;
        }
        if (reader != null) {
            reader.close();
            reader = null;
        }
    }
}
