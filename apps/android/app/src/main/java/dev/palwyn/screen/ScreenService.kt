package dev.palwyn.screen

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.graphics.Bitmap
import android.graphics.PixelFormat
import android.hardware.display.DisplayManager
import android.hardware.display.VirtualDisplay
import android.media.ImageReader
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.HandlerThread
import android.os.IBinder
import android.util.DisplayMetrics
import android.util.Log
import android.view.Display
import dev.palwyn.Prefs
import dev.palwyn.R
import dev.palwyn.link.LinkService
import java.io.ByteArrayOutputStream
import kotlin.math.max
import kotlin.math.roundToInt

/**
 * Captures the screen while the user allows it (MediaProjection) and turns changed frames into JPEGs for [Screen].
 * Android shows its own "sharing" indicator; this service's notification has Stop. Rotation resizes the capture.
 */
class ScreenService : Service() {
    private var projection: MediaProjection? = null
    private var display: VirtualDisplay? = null
    private var reader: ImageReader? = null
    private var bitmap: Bitmap? = null
    private lateinit var thread: HandlerThread
    private lateinit var handler: Handler
    private var size = Triple(0, 0, 0)
    /** Frames flow; false while paused ("keep screen sharing ready"). */
    private var live = true
    private val unwatched = Runnable { if (live && !Screen.pulled) idle() } // the PC never came for it

    private val displays by lazy { getSystemService(DisplayManager::class.java) }
    private val rotation = object : DisplayManager.DisplayListener {
        override fun onDisplayChanged(id: Int) {
            if (id == Display.DEFAULT_DISPLAY && captureSize() != size) capture()
        }
        override fun onDisplayAdded(id: Int) {}
        override fun onDisplayRemoved(id: Int) {}
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) {
            stopSelf()
            return START_NOT_STICKY
        }
        // Android 14+ wants the foreground service running before the projection is created.
        startForeground(NOTIFICATION, notification(), ServiceInfo.FOREGROUND_SERVICE_TYPE_MEDIA_PROJECTION)
        if (projection != null) return START_NOT_STICKY
        @Suppress("DEPRECATION") val data = intent?.getParcelableExtra<Intent>("data")
        if (data == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        thread = HandlerThread("pb-screen").apply { start() }
        handler = Handler(thread.looper)
        val granted = getSystemService(MediaProjectionManager::class.java).getMediaProjection(intent.getIntExtra("code", 0), data)
        if (granted == null) {
            stopSelf()
            return START_NOT_STICKY
        }
        projection = granted.apply {
            registerCallback(object : MediaProjection.Callback() {
                override fun onStop() = stopSelf() // Android's own Stop, the status bar chip, or the screen locking
            }, handler)
        }
        capture()
        displays.registerDisplayListener(rotation, handler)
        running = this
        Screen.started()
        Log.i(LinkService.TAG, "Screen sharing started: ${size.first}x${size.second}")
        handler.postDelayed(unwatched, 30_000)
        return START_NOT_STICKY
    }

    /** The screen as it is now, scaled so the long side is at most 1280 px: sharp enough, and quick to compress. */
    private fun captureSize(): Triple<Int, Int, Int> {
        val m = DisplayMetrics()
        @Suppress("DEPRECATION") displays.getDisplay(Display.DEFAULT_DISPLAY).getRealMetrics(m)
        val scale = minOf(1f, 1280f / max(m.widthPixels, m.heightPixels))
        fun even(v: Int) = ((v * scale).roundToInt() / 2) * 2
        return Triple(even(m.widthPixels), even(m.heightPixels), (m.densityDpi * scale).roundToInt())
    }

    /** Starts capturing, or follows a rotation by resizing the same virtual display (a projection allows only one). */
    private fun capture() {
        size = captureSize()
        val (w, h, dpi) = size
        val old = reader
        val next = ImageReader.newInstance(w, h, PixelFormat.RGBA_8888, 2).apply {
            setOnImageAvailableListener({ encode(it) }, handler)
        }
        reader = next
        display?.let {
            it.resize(w, h, dpi)
            it.surface = if (live) next.surface else null
        } ?: run {
            display = projection?.createVirtualDisplay("Palwyn", w, h, dpi, DisplayManager.VIRTUAL_DISPLAY_FLAG_AUTO_MIRROR, next.surface, null, handler)
        }
        old?.close()
    }

    private fun encode(r: ImageReader) {
        val image = try {
            r.acquireLatestImage()
        } catch (e: IllegalStateException) {
            null // the reader was just replaced
        } ?: return
        image.use {
            val plane = it.planes[0]
            val padded = plane.rowStride / plane.pixelStride // rows can be wider than the image
            val bmp = bitmap?.takeIf { b -> b.width == padded && b.height == it.height }
                ?: Bitmap.createBitmap(padded, it.height, Bitmap.Config.ARGB_8888).also { b -> bitmap = b }
            bmp.copyPixelsFromBuffer(plane.buffer)
            val frame = if (padded == it.width) bmp else Bitmap.createBitmap(bmp, 0, 0, it.width, it.height)
            val out = ByteArrayOutputStream(128 * 1024)
            frame.compress(Bitmap.CompressFormat.JPEG, 60, out)
            if (frame !== bmp) frame.recycle()
            if (out.size() < 1_000_000) Screen.publish(out.toByteArray()) // a stream frame has the protocol's 1 MiB limit
        }
    }

    /** Nobody is watching: pause when "keep screen sharing ready" is on, otherwise stop. */
    private fun idle() {
        if (!Prefs.screenReady(this)) {
            stopSelf()
            return
        }
        live = false
        display?.surface = null // Android stops rendering into it: no frames, little battery
        Screen.paused()
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification())
        Log.i(LinkService.TAG, "Screen sharing paused, ready for the PC")
    }

    private fun resumeCapture() {
        if (live) return
        live = true
        display?.surface = reader?.surface
        Screen.started()
        getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification())
        handler.removeCallbacks(unwatched)
        handler.postDelayed(unwatched, 30_000)
        Log.i(LinkService.TAG, "Screen sharing resumed without asking")
    }

    private fun notification(): Notification {
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Screen sharing", NotificationManager.IMPORTANCE_LOW))
        val stop = PendingIntent.getService(this, 0, Intent(this, ScreenService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE)
        return Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (live) "Showing this screen on your PC" else "Screen sharing ready for your PC")
            .setContentText(if (live) "Anything on screen is visible there." else "Your PC can show this screen again without asking. Stop to end it.")
            .setOngoing(true)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .build()
    }

    override fun onDestroy() {
        if (running === this) running = null
        if (::thread.isInitialized) {
            displays.unregisterDisplayListener(rotation)
            display?.release()
            reader?.close()
            projection?.stop()
            thread.quitSafely()
            Screen.stopped()
            Log.i(LinkService.TAG, "Screen sharing stopped")
        }
        super.onDestroy()
    }

    companion object {
        private const val CHANNEL = "screen-running"
        private const val NOTIFICATION = 51
        private const val STOP = "dev.palwyn.screen.STOP"

        @Volatile private var running: ScreenService? = null

        fun stop(c: Context) {
            c.startService(Intent(c, ScreenService::class.java).setAction(STOP))
        }

        /** The frame stream closed: pause or stop, see [idle]. */
        fun idle(c: Context) {
            val s = running ?: return stop(c)
            s.handler.post { s.idle() }
        }

        /** Resumes a paused capture; false when there is none to resume. */
        fun resume(): Boolean {
            val s = running ?: return false
            s.handler.post { s.resumeCapture() }
            return true
        }
    }
}
