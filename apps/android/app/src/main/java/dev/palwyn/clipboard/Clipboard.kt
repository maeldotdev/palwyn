package dev.palwyn.clipboard

import android.app.Activity
import android.app.PendingIntent
import android.content.ClipData
import android.content.ClipDescription
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import android.util.Log
import android.widget.Toast
import androidx.core.content.FileProvider
import dev.palwyn.Prefs
import dev.palwyn.drop.Drops
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkService
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import java.io.File

/**
 * Clipboard between phone and PC (Phase 0 section 5). PC to phone: written on receive (allowed from the
 * background). Phone to PC: Android 10+ lets only the focused app read the clipboard, so it's sent only when
 * the user taps "Send clipboard to PC" (Quick Settings tile, status notification, home screen), which reads it
 * while a screen of ours has focus, or picks "Send to PC" on selected text. The Settings switch turns both off.
 */
object ClipboardSync {
    const val MAX = 50_000
    const val MAX_IMAGE = 20L * 1024 * 1024
    private const val OFF = "Clipboard sharing is off. Turn it on in Palwyn settings."

    private val _outgoing = MutableSharedFlow<String>(extraBufferCapacity = 4)
    /** CLIPBOARD_SET texts for the connected PC. */
    val outgoing: SharedFlow<String> = _outgoing

    fun received(c: Context, text: String) {
        Handler(Looper.getMainLooper()).post {
            c.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("From your PC", text))
            // The copy happens in the background; say so, or it goes unnoticed.
            Toast.makeText(c, "Copied from your PC", Toast.LENGTH_SHORT).show()
        }
        Log.i(LinkService.TAG, "Clipboard from PC: ${text.length} chars") // never the text itself
    }

    /**
     * An image copied on the PC (TRANSFER_PUSH kind "clipboard"): stored in our cache and put on the clipboard as
     * a content URI from our FileProvider. Android lets the app that pastes it read it. Replaces the last one.
     */
    fun receiveImage(c: Context, mime: String, size: Long, read: (ByteArray, Int, Int) -> Int) {
        require(size in 1..MAX_IMAGE) { "image too large" }
        val dir = File(c.cacheDir, "clip").apply { deleteRecursively(); mkdirs() }
        val file = File(dir, "pc-${System.currentTimeMillis()}.${if (mime == "image/jpeg") "jpg" else "png"}")
        file.outputStream().use { out ->
            val buffer = ByteArray(64 * 1024)
            var left = size
            while (left > 0) {
                val n = read(buffer, 0, minOf(buffer.size.toLong(), left).toInt())
                if (n < 0) throw java.io.IOException("short image")
                out.write(buffer, 0, n)
                left -= n
            }
        }
        val uri = FileProvider.getUriForFile(c, "${c.packageName}.files", file)
        Handler(Looper.getMainLooper()).post {
            c.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newUri(c.contentResolver, "Image from your PC", uri))
            Toast.makeText(c, "Image copied from your PC", Toast.LENGTH_SHORT).show()
        }
        Log.i(LinkService.TAG, "Clipboard image from PC: ${size / 1024} KB")
    }

    /** Reads the clipboard; call only while an activity of ours has focus. Returns a message for the user. */
    fun sendCurrent(c: Context): String {
        if (!Prefs.clipboard(c)) return OFF
        if (!LinkServer.hasSessions()) return "Your PC isn't connected"
        val clip = c.getSystemService(ClipboardManager::class.java).primaryClip ?: return "The clipboard is empty"
        if (Build.VERSION.SDK_INT >= 33 && clip.description.extras?.getBoolean(ClipDescription.EXTRA_IS_SENSITIVE) == true)
            return "Passwords and other sensitive copies aren't sent"
        val item = clip.getItemAt(0) ?: return "The clipboard is empty"
        val mime = item.uri?.let { c.contentResolver.getType(it) }
        if (item.uri != null && mime?.startsWith("image/") == true) return sendImage(c, item.uri, mime)
        val text = item.coerceToText(c)?.toString()?.takeIf { it.isNotBlank() }
            ?: return "Only text and images can be sent"
        return send(c, text)
    }

    /**
     * A copied image: read now, while the clipboard grant holds, into our cache, then offered to the PC as a
     * Quick Drop marked "clipboard", which the PC puts on its clipboard.
     */
    private fun sendImage(c: Context, uri: Uri, mime: String): String {
        val dir = File(c.cacheDir, "clip-out").apply { deleteRecursively(); mkdirs() }
        val file = File(dir, "phone-${System.currentTimeMillis()}.${mime.substringAfter('/').take(8)}")
        return try {
            c.contentResolver.openInputStream(uri)?.use { input ->
                file.outputStream().use { out ->
                    if (input.copyTo(out) > MAX_IMAGE) return "That image is too big to send (over 20 MB)"
                }
            } ?: return "Couldn't read the copied image"
            Drops.offer(listOf(Drops.cacheItem(c, file, mime)), null, "clipboard")
            Log.i(LinkService.TAG, "Clipboard image to PC: ${file.length() / 1024} KB")
            "Sent to your PC. Paste it there."
        } catch (e: Exception) {
            Log.w(LinkService.TAG, "Clipboard image failed: ${e.javaClass.simpleName}")
            "Couldn't read the copied image"
        }
    }

    /** Puts [text] on the PC's clipboard. Returns a message for the user. */
    fun send(c: Context, text: String): String {
        if (!Prefs.clipboard(c)) return OFF
        if (!LinkServer.hasSessions()) return "Your PC isn't connected"
        if (text.isBlank()) return "Nothing to send"
        if (text.length > MAX) return "That's too long to send (over 50,000 characters)"
        _outgoing.tryEmit(text)
        Log.i(LinkService.TAG, "Clipboard to PC: ${text.length} chars") // never the text itself
        return "Sent to your PC. Paste it there."
    }

    fun intent(c: Context): Intent = Intent(c, ClipboardActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    fun pendingIntent(c: Context): PendingIntent =
        PendingIntent.getActivity(c, 7, intent(c), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
}

/** Invisible screen that exists only to hold focus for the moment it takes to read the clipboard. */
class ClipboardActivity : Activity() {
    private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        overridePendingTransition(0, 0)
    }

    // Focus arrives after onResume; reading earlier would be refused on Android 10+.
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus || done) return
        done = true
        Toast.makeText(this, ClipboardSync.sendCurrent(this), Toast.LENGTH_SHORT).show()
        finish()
        overridePendingTransition(0, 0)
    }
}

/** Quick Settings tile: "Send clipboard to PC". */
class ClipboardTile : TileService() {
    override fun onStartListening() {
        qsTile?.apply {
            state = if (LinkServer.hasSessions()) Tile.STATE_INACTIVE else Tile.STATE_UNAVAILABLE
            if (Build.VERSION.SDK_INT >= 29) subtitle = if (LinkServer.hasSessions()) null else "PC not connected"
            updateTile()
        }
    }

    override fun onClick() {
        if (Build.VERSION.SDK_INT >= 34) startActivityAndCollapse(ClipboardSync.pendingIntent(this))
        else @Suppress("DEPRECATION") startActivityAndCollapse(ClipboardSync.intent(this))
    }
}

/**
 * "Send to PC" in the text-selection menu of any app (ACTION_PROCESS_TEXT). Android hands us the selected text
 * directly, so no clipboard read, no focus trick: select, tap, paste on the PC.
 */
class SendTextActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val text = intent.getCharSequenceExtra(Intent.EXTRA_PROCESS_TEXT)?.toString().orEmpty()
        Toast.makeText(this, ClipboardSync.send(this, text), Toast.LENGTH_SHORT).show()
        finish()
        overridePendingTransition(0, 0)
    }
}
