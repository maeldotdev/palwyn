package dev.palwyn.drop

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.util.Log
import android.util.Patterns
import android.widget.Toast
import dev.palwyn.R
import dev.palwyn.link.LinkService
import dev.palwyn.protocol.hex
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.security.SecureRandom
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/**
 * Quick Drop. PC to phone: files land in Download/Palwyn (MediaStore, no permission needed for our own
 * files), text and links arrive as a notification. Phone to PC: things shared to Palwyn are offered to
 * the PC, which pulls each file over a transfer connection.
 */
object Drops {
    private const val CHANNEL = "drops"
    private val random = SecureRandom()
    private val notificationIds = AtomicInteger(100)

    // ---- PC to phone ----

    /** Android refuses path separators in a display name; keep a plain, printable name. */
    fun safeName(name: String): String =
        name.substringAfterLast('/').substringAfterLast('\\').filter { it >= ' ' }.trim().trimStart('.').take(200).ifEmpty { "file" }

    /** A folder path from the PC ("Trip/Day 1"), each part made safe; ".." and empty parts are dropped. */
    fun safeFolder(folder: String?): String? =
        folder?.split('/', '\\')?.map { it.filter { ch -> ch >= ' ' }.trim().trimStart('.').take(100) }
            ?.filter { it.isNotEmpty() }?.take(8)?.joinToString("/")?.ifEmpty { null }

    /**
     * Streams [size] bytes from [read] into a new Download/Palwyn file, inside [folder] when the PC sent a
     * folder. The file stays hidden (IS_PENDING) until complete and is deleted if anything fails, so a broken
     * transfer leaves nothing behind.
     */
    fun receiveFile(c: Context, name: String, mime: String, size: Long, read: (ByteArray, Int, Int) -> Int, folder: String? = null): Uri {
        val resolver = c.contentResolver
        val subfolder = safeFolder(folder)
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, safeName(name))
            put(MediaStore.MediaColumns.MIME_TYPE, mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/Palwyn" + (subfolder?.let { "/$it" } ?: ""))
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IOException("Couldn't create the file")
        try {
            resolver.openOutputStream(uri)!!.use { out ->
                val buffer = ByteArray(64 * 1024)
                var left = size
                while (left > 0) {
                    val n = read(buffer, 0, minOf(buffer.size.toLong(), left).toInt())
                    if (n < 0) throw IOException("The PC stopped with ${left} bytes to go")
                    out.write(buffer, 0, n)
                    left -= n
                }
            }
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
        } catch (e: Exception) {
            resolver.delete(uri, null, null)
            throw e
        }
        if (subfolder != null) {
            // One notification per folder, updated with each file, instead of one per file.
            val root = subfolder.substringBefore('/')
            notify(c, "Folder from your PC", "Download > Palwyn > $subfolder > ${safeName(name)}", null, 50_000 + (root.hashCode() and 0xFFFF))
        } else notify(c, "Received from your PC", safeName(name),
            PendingIntent.getActivity(c, notificationIds.get(),
                Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                    .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK),
                PendingIntent.FLAG_IMMUTABLE))
        return uri
    }

    /** Text or a link from the PC. A link opens on tap; text can be copied (user-initiated, so no clipboard surprise). */
    fun text(c: Context, text: String) {
        val trimmed = text.trim()
        val link = trimmed.takeIf { (it.startsWith("https://") || it.startsWith("http://")) && Patterns.WEB_URL.matcher(it).matches() }
        if (link != null) {
            notify(c, "Link from your PC", link, PendingIntent.getActivity(c, notificationIds.get(),
                Intent(Intent.ACTION_VIEW, Uri.parse(link)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK), PendingIntent.FLAG_IMMUTABLE))
            return
        }
        val id = notificationIds.incrementAndGet()
        val copy = PendingIntent.getBroadcast(c, id,
            Intent(c, CopyReceiver::class.java).putExtra(CopyReceiver.TEXT, trimmed).putExtra(CopyReceiver.NOTIFICATION, id),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        notify(c, "Text from your PC", trimmed, null, id,
            Notification.Action.Builder(null, "Copy", copy).build())
    }

    private fun notify(c: Context, title: String, text: String, tap: PendingIntent?,
                       id: Int = notificationIds.incrementAndGet(), vararg actions: Notification.Action) {
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Received from your PC", NotificationManager.IMPORTANCE_DEFAULT))
        nm.notify(id, Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setStyle(Notification.BigTextStyle().bigText(text.take(5000)))
            .setAutoCancel(true)
            .apply { tap?.let { setContentIntent(it) }; actions.forEach { addAction(it) } }
            .build())
    }

    // ---- Phone to PC ----

    class Item(val uri: Uri, val name: String, val size: Long, val mime: String)

    /** Progress of one offer, for the share screen. [ok] is null while running. */
    data class Progress(val sent: Long, val total: Long, val ok: Boolean? = null)

    class Offer(val id: String, val items: List<Item>, val text: String?) {
        val sent = AtomicLong()
        val state = MutableStateFlow(Progress(0, items.sumOf { it.size }))
    }

    private val offers = ConcurrentHashMap<String, Offer>()
    private val _outgoing = MutableSharedFlow<JSONObject>(extraBufferCapacity = 16)
    /** DROP_OFFER payloads for the connected PC. */
    val outgoing: SharedFlow<JSONObject> = _outgoing

    // ponytail: offered to every connected PC; with more than one they'd all pull it. One PC is the only setup today.
    /** [purpose]: null for a share, "clipboard" (the PC puts it on its clipboard) or "camera" (a photo the PC asked for). */
    fun offer(items: List<Item>, text: String?, purpose: String? = null): Offer {
        val offer = Offer(ByteArray(8).also(random::nextBytes).hex(), items, text)
        offers[offer.id] = offer
        _outgoing.tryEmit(JSONObject()
            .put("dropId", offer.id)
            .put("files", JSONArray(items.mapIndexed { i, it ->
                JSONObject().put("index", i).put("name", it.name).put("size", it.size).put("mime", it.mime)
            }))
            .apply { text?.let { put("text", it.take(50_000)) }; purpose?.let { put("purpose", it) } })
        Log.i(LinkService.TAG, "Offered ${items.size} files to the PC")
        return offer
    }

    /** A file in our cache as an offer item, readable through our FileProvider (files in cache/clip and cache/camera). */
    fun cacheItem(c: Context, file: java.io.File, mime: String) =
        Item(androidx.core.content.FileProvider.getUriForFile(c, "${c.packageName}.files", file), file.name, file.length(), mime)

    fun cancel(offer: Offer) {
        offers.remove(offer.id)
    }

    /** A file the PC may pull: only items of a live offer, identified as "dropId:index". */
    fun find(id: String): Pair<Offer, Item>? {
        val offer = offers[id.substringBefore(':')] ?: return null
        val item = id.substringAfter(':').toIntOrNull()?.let { offer.items.getOrNull(it) } ?: return null
        return offer to item
    }

    fun sent(offer: Offer, bytes: Long) {
        val total = offer.sent.addAndGet(bytes)
        offer.state.value = offer.state.value.copy(sent = total)
    }

    /** DROP_RESULT from the PC: it has everything (or gave up). */
    fun finished(dropId: String, ok: Boolean) {
        val offer = offers.remove(dropId) ?: return
        offer.state.value = offer.state.value.copy(ok = ok)
        Log.i(LinkService.TAG, "Drop to PC ${if (ok) "done" else "failed"}")
    }
}

/** "Copy" on a "Text from your PC" notification. */
class CopyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val text = intent.getStringExtra(TEXT) ?: return
        context.getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("From your PC", text))
        context.getSystemService(NotificationManager::class.java).cancel(intent.getIntExtra(NOTIFICATION, 0))
        // Android 13+ confirms clipboard writes itself.
        if (android.os.Build.VERSION.SDK_INT < 33) Toast.makeText(context, "Copied", Toast.LENGTH_SHORT).show()
    }

    companion object {
        const val TEXT = "text"
        const val NOTIFICATION = "notification"
    }
}
