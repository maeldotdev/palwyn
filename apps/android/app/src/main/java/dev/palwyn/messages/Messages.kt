package dev.palwyn.messages

import android.Manifest
import android.app.Activity
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.ContentUris
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.database.ContentObserver
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import android.provider.Telephony.Mms
import android.provider.Telephony.Sms
import android.telephony.SmsManager
import android.util.Log
import androidx.core.content.FileProvider
import dev.palwyn.device.Contacts
import dev.palwyn.link.LinkService
import dev.palwyn.photos.Photos
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.suspendCancellableCoroutine
import kotlinx.coroutines.withTimeoutOrNull
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.atomic.AtomicInteger
import kotlin.coroutines.resume

/**
 * SMS and MMS from the phone's own store (verified readable on the reference phone in Phase 0). New messages
 * are noticed with ContentObservers on the SMS and MMS providers, which fire when the default SMS app stores
 * an incoming message and when anything is sent, so only READ_SMS is needed (no RECEIVE_SMS/RECEIVE_MMS).
 *
 * Not covered: RCS "chat" messages, which aren't in the store at all (Phase 0: no public RCS API).
 * A non-default SMS app also can't mark messages read.
 */
object Messages {
    const val BODY_MAX = 10_000
    private const val PENDING_GRACE_MS = 5 * 60_000L
    private const val MAX_PARTS = 10
    // MMS message types (OMA MMS encapsulation): sent, received and not-yet-downloaded.
    private const val M_SEND_REQ = 128
    private const val M_NOTIFICATION_IND = 130
    private const val M_RETRIEVE_CONF = 132
    private const val ADDR_FROM = 137
    private const val ADDR_TO = 151
    private val MMS_PART: Uri = Uri.parse("content://mms/part")

    private val _added = MutableSharedFlow<JSONObject>(extraBufferCapacity = 64)
    /** New SMS_RECEIVED payloads, incoming and sent. */
    val added: SharedFlow<JSONObject> = _added

    private lateinit var app: Context
    private var observer: ContentObserver? = null
    private var worker: HandlerThread? = null
    /** Highest SMS and MMS _ids already reported; -1 until the first scan with READ_SMS. */
    private var lastId = -1L
    private var lastMmsId = -1L
    private val sendIds = AtomicInteger()

    fun start(c: Context) {
        if (observer != null) return
        app = c.applicationContext
        val thread = HandlerThread("pb-sms").also { it.start() }
        worker = thread
        observer = object : ContentObserver(Handler(thread.looper)) {
            override fun onChange(selfChange: Boolean) = scan()
        }.also {
            app.contentResolver.registerContentObserver(Sms.CONTENT_URI, true, it)
            app.contentResolver.registerContentObserver(Mms.CONTENT_URI, true, it)
        }
        Handler(thread.looper).post { scan() } // sets the baseline, so old messages are never announced
    }

    fun stop(c: Context) {
        observer?.let { c.applicationContext.contentResolver.unregisterContentObserver(it) }
        observer = null
        worker?.quitSafely()
        worker = null
    }

    /** READ_SMS may just have been granted: take the baseline now so the next new message isn't swallowed by it. */
    fun permissionsChanged() {
        worker?.let { Handler(it.looper).post { scan() } }
    }

    private fun canRead() = app.checkSelfPermission(Manifest.permission.READ_SMS) == PackageManager.PERMISSION_GRANTED

    private fun scan() {
        if (!canRead()) return
        try {
            if (lastId < 0) {
                lastId = app.contentResolver.query(Sms.CONTENT_URI, arrayOf(Sms._ID), null, null, "${Sms._ID} DESC")
                    ?.use { if (it.moveToFirst()) it.getLong(0) else 0 } ?: 0
                lastMmsId = app.contentResolver.query(Mms.CONTENT_URI, arrayOf(Mms._ID), null, null, "${Mms._ID} DESC")
                    ?.use { if (it.moveToFirst()) it.getLong(0) else 0 } ?: 0
                return
            }
            scanMms()
            app.contentResolver.query(
                Sms.CONTENT_URI,
                arrayOf(Sms._ID, Sms.THREAD_ID, Sms.ADDRESS, Sms.BODY, Sms.DATE, Sms.TYPE),
                "${Sms._ID} > ?", arrayOf("$lastId"), "${Sms._ID} ASC",
            )?.use { c ->
                while (c.moveToNext()) {
                    val type = c.getInt(5)
                    val date = c.getLong(4)
                    // A message still sending turns into SENT in place; wait for that rather than skip it.
                    if ((type == Sms.MESSAGE_TYPE_OUTBOX || type == Sms.MESSAGE_TYPE_QUEUED) &&
                        System.currentTimeMillis() - date < PENDING_GRACE_MS
                    ) break
                    lastId = c.getLong(0)
                    if (type != Sms.MESSAGE_TYPE_INBOX && type != Sms.MESSAGE_TYPE_SENT) continue
                    val address = c.getString(2) ?: continue
                    val outgoing = type == Sms.MESSAGE_TYPE_SENT
                    Log.i(LinkService.TAG, "SMS ${if (outgoing) "sent" else "received"} ${mask(address)}")
                    _added.tryEmit(JSONObject()
                        .put("threadId", c.getLong(1).toString())
                        .put("messageId", lastId.toString())
                        .put("address", address.take(64))
                        .put("body", (c.getString(3) ?: "").take(BODY_MAX))
                        .put("date", date)
                        .put("outgoing", outgoing)
                        .apply { Contacts.name(app, address)?.let { put("name", it.take(128)) } })
                }
            }
        } catch (e: RuntimeException) {
            Log.w(LinkService.TAG, "SMS scan failed: ${e.javaClass.simpleName}")
        }
    }

    fun mask(address: String) = "••" + address.takeLast(2)

    /** New MMS since the last scan, announced like SMS. Waits for one still downloading or sending, as for SMS. */
    private fun scanMms() {
        app.contentResolver.query(
            Mms.CONTENT_URI, arrayOf(Mms._ID, Mms.THREAD_ID, Mms.MESSAGE_BOX, Mms.MESSAGE_TYPE, Mms.DATE, Mms.READ),
            "${Mms._ID} > ?", arrayOf("$lastMmsId"), "${Mms._ID} ASC",
        )?.use { c ->
            while (c.moveToNext()) {
                val box = c.getInt(2)
                val type = c.getInt(3)
                val date = c.getLong(4) * 1000
                if ((type == M_NOTIFICATION_IND || box == Mms.MESSAGE_BOX_OUTBOX) &&
                    System.currentTimeMillis() - date < PENDING_GRACE_MS
                ) break
                lastMmsId = c.getLong(0)
                if (type != M_SEND_REQ && type != M_RETRIEVE_CONF) continue
                val m = mms(lastMmsId, box, date, c.getInt(5) != 0) ?: continue
                val address = m.getString("address")
                Log.i(LinkService.TAG, "MMS ${if (m.getBoolean("outgoing")) "sent" else "received"} ${mask(address)}")
                m.remove("read")
                m.put("threadId", c.getLong(1).toString())
                Contacts.name(app, address)?.let { m.put("name", it.take(128)) }
                _added.tryEmit(m)
            }
        }
    }

    /**
     * One MMS as an SMS_MESSAGES item: its text parts joined as the body, other parts (pictures, video, audio,
     * contact cards) listed in `parts` for TRANSFER_PULL kind "mms". The address is the sender, or the first
     * recipient of one sent from this phone. Null for drafts.
     */
    private fun mms(id: Long, box: Int, date: Long, read: Boolean): JSONObject? {
        val status = when (box) {
            Mms.MESSAGE_BOX_INBOX, Mms.MESSAGE_BOX_SENT -> null
            Mms.MESSAGE_BOX_OUTBOX -> "sending"
            Mms.MESSAGE_BOX_FAILED -> "failed"
            else -> return null
        }
        val body = StringBuilder()
        val parts = JSONArray()
        app.contentResolver.query(MMS_PART, arrayOf("_id", "ct", "name", "fn", "text"), "mid = ?", arrayOf("$id"), "seq")?.use { p ->
            while (p.moveToNext()) {
                val mime = p.getString(1)?.lowercase() ?: continue
                when {
                    mime == "application/smil" -> {} // layout only
                    mime == "text/plain" -> {
                        val text = p.getString(4) ?: runCatching {
                            app.contentResolver.openInputStream(ContentUris.withAppendedId(MMS_PART, p.getLong(0)))?.use { it.reader().readText() }
                        }.getOrNull()
                        if (!text.isNullOrBlank()) body.append(if (body.isEmpty()) "" else "\n").append(text)
                    }
                    parts.length() < MAX_PARTS -> parts.put(JSONObject().put("id", p.getLong(0).toString()).put("mime", mime.take(127))
                        .apply { (p.getString(3) ?: p.getString(2))?.takeIf { it.isNotBlank() }?.let { put("name", it.take(255)) } })
                }
            }
        }
        val outgoing = box != Mms.MESSAGE_BOX_INBOX
        val address = app.contentResolver.query(
            Uri.parse("content://mms/$id/addr"), arrayOf("address"), "type = ?", arrayOf("${if (outgoing) ADDR_TO else ADDR_FROM}"), null,
        )?.use { if (it.moveToFirst()) it.getString(0) else null } ?: ""
        return JSONObject()
            .put("messageId", "mms-$id")
            .put("address", address.take(64))
            .put("body", body.toString().take(BODY_MAX))
            .put("date", date)
            .put("outgoing", outgoing)
            .put("read", read)
            .apply { status?.let { put("status", it) } }
            .apply { if (parts.length() > 0) put("parts", parts) }
    }

    /** An MMS attachment for TRANSFER_PULL kind "mms": only parts of real MMS (not SMIL or text) are served. */
    fun part(partId: Long): Photos.Original? {
        val (mime, name) = app.contentResolver.query(
            MMS_PART, arrayOf("ct", "fn", "name"), "_id = ?", arrayOf("$partId"), null,
        )?.use { if (it.moveToFirst()) (it.getString(0)?.lowercase() ?: "") to (it.getString(1) ?: it.getString(2)) else null } ?: return null
        if (mime.isEmpty() || mime == "application/smil" || mime == "text/plain") return null
        // ponytail: whole part in memory; MMS carrier limits keep these to a few MB at most.
        val bytes = app.contentResolver.openInputStream(ContentUris.withAppendedId(MMS_PART, partId))?.use { it.readBytes() } ?: return null
        val ext = android.webkit.MimeTypeMap.getSingleton().getExtensionFromMimeType(mime) ?: "bin"
        return Photos.Original(name?.takeIf { it.isNotBlank() } ?: "mms-$partId.$ext", mime, bytes.size.toLong(), bytes.inputStream())
    }

    /**
     * Conversations, newest first. Built from the provider's thread table, as the phone's own SMS app does.
     * With [query], only those whose contact name or number matches it or that have an SMS containing it;
     * the snippet is then the newest such SMS, so the PC shows why it matched.
     */
    fun threads(limit: Int, before: Long?, query: String? = null): JSONArray {
        val resolver = app.contentResolver
        val hits = HashMap<String, String>()
        if (query != null) {
            val like = "%" + query.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_") + "%"
            resolver.query(
                Sms.CONTENT_URI, arrayOf(Sms.THREAD_ID, Sms.BODY),
                "${Sms.BODY} LIKE ? ESCAPE '\\'", arrayOf(like), "${Sms.DATE} DESC",
            )?.use { while (it.moveToNext()) hits.putIfAbsent(it.getLong(0).toString(), it.getString(1) ?: "") }
        }
        val canonical = HashMap<String, String>()
        resolver.query(Uri.parse("content://mms-sms/canonical-addresses"), arrayOf("_id", "address"), null, null, null)
            ?.use { while (it.moveToNext()) canonical[it.getLong(0).toString()] = it.getString(1) ?: "" }
        val names = HashMap<String, String?>()
        val out = JSONArray()
        resolver.query(
            Uri.parse("content://mms-sms/conversations?simple=true"),
            arrayOf("_id", "date", "recipient_ids", "snippet", "read", "message_count"),
            before?.let { "date < ?" }, before?.let { arrayOf("$it") }, "date DESC",
        )?.use { c ->
            while (c.moveToNext() && out.length() < limit) {
                if (c.getInt(5) == 0) continue
                val addresses = (c.getString(2) ?: "").split(' ').mapNotNull { canonical[it]?.takeIf(String::isNotBlank) }.take(20)
                val threadNames = addresses.map { a -> names.getOrPut(a) { Contacts.name(app, a) }?.take(128) ?: "" }
                val threadId = c.getLong(0).toString()
                if (query != null && threadId !in hits &&
                    !Contacts.matches(query, threadNames.joinToString(" "), addresses)
                ) continue
                out.put(JSONObject()
                    .put("threadId", threadId)
                    .put("addresses", JSONArray(addresses.map { it.take(64) }))
                    .put("names", JSONArray(threadNames))
                    .put("snippet", (hits[threadId] ?: c.getString(3)?.takeIf { it.isNotBlank() } ?: mmsSnippet(threadId)).take(200))
                    .put("date", c.getLong(1))
                    .put("unread", c.getInt(4) == 0))
            }
        }
        return out
    }

    /** The provider's snippet for an MMS is its subject, usually empty: use the newest MMS's text instead. */
    private fun mmsSnippet(threadId: String): String =
        app.contentResolver.query(
            Mms.CONTENT_URI, arrayOf(Mms._ID, Mms.MESSAGE_BOX, Mms.DATE), "${Mms.THREAD_ID} = ?", arrayOf(threadId), "${Mms.DATE} DESC",
        )?.use { c ->
            if (!c.moveToFirst()) return@use null
            val m = mms(c.getLong(0), c.getInt(1), 0, true) ?: return@use null
            m.getString("body").ifBlank { if (m.has("parts")) "Attachment" else "" }
        } ?: ""

    /** One conversation's SMS and MMS, newest first. */
    fun messages(threadId: String, limit: Int, before: Long?): JSONArray {
        val all = ArrayList<JSONObject>()
        app.contentResolver.query(
            Mms.CONTENT_URI, arrayOf(Mms._ID, Mms.MESSAGE_BOX, Mms.DATE, Mms.READ, Mms.MESSAGE_TYPE),
            // MMS dates are in seconds: a message in `before`'s own second is still older than it.
            "${Mms.THREAD_ID} = ?" + (before?.let { " AND ${Mms.DATE} < ?" } ?: ""),
            listOfNotNull(threadId, before?.let { "${(it + 999) / 1000}" }).toTypedArray(),
            "${Mms.DATE} DESC",
        )?.use { c ->
            while (c.moveToNext() && all.size < limit) {
                // ponytail: an MMS not downloaded yet isn't listed; it shows once the SMS app downloads it.
                if (c.getInt(4) == M_NOTIFICATION_IND) continue
                mms(c.getLong(0), c.getInt(1), c.getLong(2) * 1000, c.getInt(3) != 0)?.let(all::add)
            }
        }
        sms(threadId, limit, before, all)
        all.sortByDescending { it.getLong("date") }
        val out = JSONArray()
        // Frames are capped at 1 MiB: stop well before, counting 4 bytes per char as the worst case.
        var budget = 800_000
        for (m in all.take(limit)) {
            budget -= m.getString("body").length * 4 + 200 + (m.optJSONArray("parts")?.length() ?: 0) * 400
            if (budget < 0 && out.length() > 0) break
            out.put(m)
        }
        return out
    }

    private fun sms(threadId: String, limit: Int, before: Long?, into: MutableList<JSONObject>) {
        var count = 0
        app.contentResolver.query(
            Sms.CONTENT_URI,
            arrayOf(Sms._ID, Sms.ADDRESS, Sms.BODY, Sms.DATE, Sms.TYPE, Sms.READ),
            "${Sms.THREAD_ID} = ?" + (before?.let { " AND ${Sms.DATE} < ?" } ?: ""),
            listOfNotNull(threadId, before?.toString()).toTypedArray(),
            "${Sms.DATE} DESC",
        )?.use { c ->
            while (c.moveToNext() && count++ < limit) {
                val status = when (c.getInt(4)) {
                    Sms.MESSAGE_TYPE_INBOX, Sms.MESSAGE_TYPE_SENT -> null
                    Sms.MESSAGE_TYPE_OUTBOX, Sms.MESSAGE_TYPE_QUEUED -> "sending"
                    Sms.MESSAGE_TYPE_FAILED -> "failed"
                    else -> continue // drafts
                }
                val body = (c.getString(2) ?: "").take(BODY_MAX)
                into.add(JSONObject()
                    .put("messageId", c.getLong(0).toString())
                    .put("address", (c.getString(1) ?: "").take(64))
                    .put("body", body)
                    .put("date", c.getLong(3))
                    .put("outgoing", c.getInt(4) != Sms.MESSAGE_TYPE_INBOX)
                    .put("read", c.getInt(5) != 0)
                    .apply { status?.let { put("status", it) } })
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun smsManager(): SmsManager =
        if (Build.VERSION.SDK_INT >= 31) app.getSystemService(SmsManager::class.java) else SmsManager.getDefault()

    /**
     * Sends through the phone's default SMS subscription and waits for the radio's result. Android stores
     * the sent message itself for non-default SMS apps, so it then shows up through [added].
     * Returns null on success, else a protocol error code.
     */
    suspend fun send(address: String, body: String): String? {
        if (app.checkSelfPermission(Manifest.permission.SEND_SMS) != PackageManager.PERMISSION_GRANTED) return "PERMISSION_DENIED"
        if (body.isBlank()) return "FAILED"
        val sms = smsManager()
        val parts = sms.divideMessage(body)
        val result = radio(parts.size, 60_000) { sent -> sms.sendMultipartTextMessage(address, null, parts, ArrayList(sent), null) }
        Log.i(LinkService.TAG, "SMS to ${mask(address)}: ${when (result) { true -> "sent"; false -> "failed"; null -> "no result in 60 s" }}")
        return if (result == true) null else "FAILED"
    }

    /**
     * A group text: one MMS to every address, as the phone's own SMS app sends group replies. Needs only
     * SEND_SMS; Android's MMS service fetches the carrier's MMS settings, brings up mobile data for the
     * MMS APN, and (for a non-default SMS app) stores the sent MMS, which then shows up through [added].
     */
    suspend fun sendGroup(addresses: List<String>, body: String): String? {
        if (app.checkSelfPermission(Manifest.permission.SEND_SMS) != PackageManager.PERMISSION_GRANTED) return "PERMISSION_DENIED"
        if (body.isBlank() || addresses.isEmpty()) return "FAILED"
        val id = sendIds.incrementAndGet()
        val dir = File(app.cacheDir, "mms").apply { mkdirs() }
        val file = File(dir, "send-$id.pdu")
        val result = try {
            file.writeBytes(MmsPdu.sendReq(addresses, body, "pb${System.currentTimeMillis()}$id", System.currentTimeMillis() / 1000))
            val uri = FileProvider.getUriForFile(app, "${app.packageName}.files", file)
            radio(1, 120_000) { sent -> smsManager().sendMultimediaMessage(app, uri, null, null, sent[0]) }
        } finally {
            file.delete()
        }
        Log.i(LinkService.TAG, "MMS to ${addresses.size} people: ${when (result) { true -> "sent"; false -> "failed"; null -> "no result in 120 s" }}")
        return if (result == true) null else "FAILED"
    }

    /** Starts a send with [count] result intents and waits for all of them: true if every one reported OK. Null on timeout. */
    private suspend fun radio(count: Int, timeoutMs: Long, start: (List<PendingIntent>) -> Unit): Boolean? {
        val action = "${app.packageName}.SMS_SENT.${sendIds.incrementAndGet()}"
        return withTimeoutOrNull(timeoutMs) {
            suspendCancellableCoroutine { cont ->
                var pending = count
                var ok = true
                val receiver = object : BroadcastReceiver() {
                    override fun onReceive(context: Context, intent: Intent) {
                        if (resultCode != Activity.RESULT_OK) ok = false
                        if (--pending == 0) {
                            app.unregisterReceiver(this)
                            if (cont.isActive) cont.resume(ok)
                        }
                    }
                }
                if (Build.VERSION.SDK_INT >= 33) app.registerReceiver(receiver, IntentFilter(action), Context.RECEIVER_NOT_EXPORTED)
                else app.registerReceiver(receiver, IntentFilter(action))
                cont.invokeOnCancellation { runCatching { app.unregisterReceiver(receiver) } }
                val intents = (0 until count).map {
                    PendingIntent.getBroadcast(app, it, Intent(action).setPackage(app.packageName),
                        PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_ONE_SHOT)
                }
                try {
                    start(intents)
                } catch (e: RuntimeException) { // no SIM, invalid address, no default subscription
                    Log.w(LinkService.TAG, "Send refused: ${e.javaClass.simpleName}")
                    runCatching { app.unregisterReceiver(receiver) }
                    if (cont.isActive) cont.resume(false)
                }
            }
        }
    }
}
