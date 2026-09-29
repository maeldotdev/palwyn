package dev.palwyn.screen

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.media.projection.MediaProjectionManager
import android.os.Bundle
import android.util.Log
import android.widget.Toast
import dev.palwyn.R
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkService
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.DataOutputStream
import java.io.IOException
import java.io.OutputStream
import kotlin.concurrent.thread

/**
 * The phone's screen on a PC (docs/research-screen-mirroring.md). Android lets only the user start a capture, so a
 * PC's SCREEN_REQUEST shows a notification that leads to Android's own consent dialog. Frames then go as JPEGs on a
 * connection of their own, which only the PC that asked may open.
 */
object Screen {
    private const val CHANNEL = "screen"
    private const val NOTIFICATION = 50

    /** The PC the screen is (being) shared with, by fingerprint hex. */
    @Volatile var requester: String? = null
        private set
    private val _capturing = MutableStateFlow(false)
    val capturing: StateFlow<Boolean> = _capturing
    /** The PC opened the frame stream; a capture nobody watches stops (or pauses) after 30 s. */
    @Volatile var pulled = false
        private set
    /** Consent given and the capture kept, but no frames: "keep screen sharing ready" is on and nobody is watching. */
    @Volatile var paused = false
        private set

    // Only the newest frame is kept: a slow link skips frames instead of falling behind.
    private val lock = Object()
    private var frame: ByteArray? = null
    private var sequence = 0L

    fun request(c: Context, pc: String) {
        if (_capturing.value && requester != pc) return // already shared with another PC
        if (paused) {
            // Ready from an earlier consent: the same PC gets the screen back without asking again.
            if (requester == pc && ScreenService.resume()) return
            ScreenService.stop(c) // kept for another PC: that consent doesn't cover this one
        }
        requester = pc
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Screen sharing requests from your PC", NotificationManager.IMPORTANCE_HIGH))
        val open = PendingIntent.getActivity(c, NOTIFICATION, Intent(c, ScreenActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        nm.notify(NOTIFICATION, Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Show this screen on your PC?")
            .setContentText("Tap, then choose Start in the next dialog. You can stop any time.")
            .setContentIntent(open)
            .setAutoCancel(true)
            .setTimeoutAfter(120_000)
            .build())
        Log.i(LinkService.TAG, "Screen requested by a PC")
    }

    fun dismiss(c: Context) = c.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)

    internal fun started() {
        pulled = false
        paused = false
        _capturing.value = true
        LinkServer.permissionsChanged() // screen.control may now be on
        tell("started")
    }

    /** Sharing paused but kept (see [paused]): the PC is told it stopped, and control is off while nobody watches. */
    internal fun paused() {
        paused = true
        _capturing.value = false
        synchronized(lock) {
            frame = null
            lock.notifyAll()
        }
        LinkServer.permissionsChanged()
        tell("stopped")
    }

    internal fun stopped() {
        paused = false
        _capturing.value = false
        synchronized(lock) {
            frame = null
            lock.notifyAll()
        }
        LinkServer.permissionsChanged()
        tell("stopped")
        requester = null
    }

    internal fun declined() {
        tell("declined")
        requester = null
    }

    internal fun publish(jpeg: ByteArray) = synchronized(lock) {
        frame = jpeg
        sequence++
        lock.notifyAll()
    }

    private fun tell(state: String) {
        val pc = requester ?: return
        thread(name = "pb-screen-state") {
            try {
                LinkServer.send(pc, "SCREEN_STATE", JSONObject().put("state", state))
            } catch (e: IOException) { /* gone; it notices the link drop */ }
        }
    }

    /**
     * SCREEN_PULL from [pc]: writes frames until the capture stops or the PC closes the connection, which also
     * stops the capture. Each frame is a big-endian u32 length and the JPEG, like a protocol frame without JSON.
     */
    fun stream(c: Context, pc: String, out: OutputStream): Boolean {
        if (!_capturing.value || pc != requester) return false
        pulled = true
        val data = DataOutputStream(BufferedOutputStream(out, 256 * 1024))
        var seen = -1L
        try {
            while (true) {
                val next = synchronized(lock) {
                    while (_capturing.value && (frame == null || sequence == seen)) lock.wait(1000)
                    if (!_capturing.value) null else frame.also { seen = sequence }
                } ?: break
                data.writeInt(next.size)
                data.write(next)
                data.flush()
            }
        } catch (e: IOException) {
            Log.i(LinkService.TAG, "Screen stream closed by the PC")
        } finally {
            ScreenService.idle(c)
        }
        return true
    }
}

/** Opens Android's screen-capture consent dialog; on Start, the capture runs in [ScreenService]. */
class ScreenActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState != null) return // recreated while the dialog is up: its result is still coming
        Screen.dismiss(this)
        @Suppress("DEPRECATION")
        startActivityForResult(getSystemService(MediaProjectionManager::class.java).createScreenCaptureIntent(), 1)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        when {
            resultCode != RESULT_OK || data == null -> Screen.declined()
            !LinkServer.hasSessions() -> {
                Toast.makeText(this, "Your PC isn't connected", Toast.LENGTH_SHORT).show()
                Screen.declined()
            }
            else -> startForegroundService(Intent(this, ScreenService::class.java).putExtra("code", resultCode).putExtra("data", data))
        }
        finish()
    }
}
