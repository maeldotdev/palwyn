package dev.palwyn.protocol

import org.json.JSONArray
import org.json.JSONException
import org.json.JSONObject
import java.io.BufferedInputStream
import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.charset.CharacterCodingException
import java.nio.charset.CodingErrorAction
import java.security.SecureRandom

enum class Side { Phone, Pc }

sealed interface Verdict {
    data object Accept : Verdict
    data object Close : Verdict
    data class Error(val code: String, val id: String) : Verdict
}

class BadFrameException(message: String) : Exception(message)

/** Envelope rules from docs/protocol.md section 3, in order; first failure wins. Mirrors the C# implementation. */
object Envelope {
    const val VERSION = 1
    const val MAX_FRAME = 1 shl 20
    private val ID = Regex("^[A-Za-z0-9_-]{1,64}$")
    private val TYPE = Regex("^[A-Z][A-Z0-9_]{0,63}$")
    private val random = SecureRandom()

    fun lengthOk(length: Int) = length in 1..MAX_FRAME

    fun create(type: String, payload: JSONObject = JSONObject(), replyTo: String? = null): JSONObject =
        JSONObject()
            .put("v", VERSION)
            .put("id", ByteArray(8).also(random::nextBytes).hex())
            .put("type", type)
            .put("ts", System.currentTimeMillis())
            .put("payload", payload)
            .apply { if (replyTo != null) put("replyTo", replyTo) }

    fun validate(body: ByteArray, receiver: Side, capabilities: Set<String>, version: Int = VERSION): Pair<Verdict, JSONObject?> {
        val text = try {
            Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                .decode(ByteBuffer.wrap(body)).toString()
        } catch (e: CharacterCodingException) {
            return Verdict.Close to null
        }
        val o = try {
            JSONObject(text)
        } catch (e: JSONException) {
            return Verdict.Close to null
        }
        val id = o.str("id")
        val type = o.str("type")
        val payload = o.value("payload") as? JSONObject
        val replyTo = o.value("replyTo")
        if (!o.isInt("v") || id == null || !ID.matches(id) || type == null || !TYPE.matches(type) ||
            !o.isInt("ts") || payload == null || (replyTo != null && replyTo !is String)
        ) return Verdict.Close to null

        if ((o.value("v") as Number).toLong() != version.toLong()) return Verdict.Close to null
        val spec = Catalog.types[type]
        if (spec == null || !spec.receivableBy(receiver)) return Verdict.Error("UNSUPPORTED_TYPE", id) to null
        if (!spec.payloadValid(payload)) return Verdict.Error("INVALID_PAYLOAD", id) to null
        if (spec.capability != null && spec.capability !in capabilities) return Verdict.Error("NOT_CAPABLE", id) to null
        return Verdict.Accept to o
    }
}

/** Length-prefixed (u32 big-endian) JSON frames. Writes are serialized. */
class Frames(input: InputStream, output: OutputStream) {
    private val inp = DataInputStream(BufferedInputStream(input))
    private val out = DataOutputStream(BufferedOutputStream(output))

    /** The next frame body, or null on a clean end of stream. */
    fun read(): ByteArray? {
        val length = try {
            inp.readInt()
        } catch (e: EOFException) {
            return null
        }
        if (!Envelope.lengthOk(length)) throw BadFrameException("bad frame length $length")
        return ByteArray(length).also(inp::readFully)
    }

    /** Raw bytes after a frame on a transfer connection, from the same buffered stream so none are skipped. */
    fun readRaw(buffer: ByteArray, offset: Int, length: Int): Int = inp.read(buffer, offset, length)

    @Synchronized
    fun write(message: JSONObject) {
        val body = message.toString().toByteArray(Charsets.UTF_8)
        if (!Envelope.lengthOk(body.size)) throw BadFrameException("frame too large")
        out.writeInt(body.size)
        out.write(body)
        out.flush()
    }
}

class TypeSpec(val sender: Side?, val capability: String?, val payloadValid: (JSONObject) -> Boolean) {
    fun receivableBy(receiver: Side) = sender == null || sender != receiver
}

/** Message types from docs/protocol.md; must match the C# Catalog. */
object Catalog {
    private val P = Side.Phone
    private val C = Side.Pc
    private val any: (JSONObject) -> Boolean = { true }

    val types: Map<String, TypeSpec> = mapOf(
        "HELLO" to TypeSpec(null, null) { p ->
            val pr = p.value("protocol") as? JSONObject
            pr != null && pr.int("min", 1, 1000) && pr.int("max", 1, 1000) && p.str("app", 32) &&
                p.hex("deviceId", 32) && p.str("name", 64) && p.oneOf("platform", "android", "windows") &&
                p.strArray("capabilities") && p.int("keepAlive", 1, 300, optional = true)
        },
        "PING" to TypeSpec(null, null, any),
        "PONG" to TypeSpec(null, null, any),
        "ERROR" to TypeSpec(null, null) { it.str("code", 64) && it.str("message", 500, optional = true) },
        "CAPABILITIES_CHANGED" to TypeSpec(null, null) { it.strArray("capabilities") },
        "UNPAIR" to TypeSpec(null, null, any),

        "DEVICE_INFO" to TypeSpec(P, "device") { p ->
            p.str("manufacturer", 64) && p.str("model", 64) && p.str("androidVersion", 32) && p.int("sdk", 1, 1000) &&
                p.int("storageTotal", 0, Long.MAX_VALUE) && p.int("storageFree", 0, Long.MAX_VALUE)
        },
        "BATTERY_CHANGED" to TypeSpec(P, "device") { it.int("level", 0, 100) && it.bool("charging") },

        "CALL_STATE" to TypeSpec(P, "calls.state") { p ->
            p.str("callId", 64) && p.oneOf("state", "RINGING", "ACTIVE", "ENDED") && p.oneOf("direction", "IN", "OUT") &&
                p.int("since", 0, Long.MAX_VALUE) && p.str("number", 64, optional = true) && p.str("name", 128, optional = true)
        },
        "CALL_ANSWER" to TypeSpec(C, "calls.control") { it.str("callId", 64) },
        "CALL_DECLINE" to TypeSpec(C, "calls.control") { it.str("callId", 64) },
        "CALL_END" to TypeSpec(C, "calls.control") { it.str("callId", 64) },
        "CALL_RESULT" to TypeSpec(P, "calls.control") { it.bool("ok") },
        "CALL_LOG_GET" to TypeSpec(C, "calls.log") { p ->
            p.int("limit", 1, 100) && p.int("before", 0, Long.MAX_VALUE, optional = true) && p.str("query", 100, optional = true)
        },
        "CALL_LOG" to TypeSpec(P, "calls.log") { p ->
            p.objArray("calls", 100) { c ->
                c.str("id", 32) && c.str("number", 64, optional = true) && c.str("name", 128, optional = true) &&
                    c.oneOf("type", "IN", "OUT", "MISSED", "REJECTED", "BLOCKED", "VOICEMAIL") &&
                    c.int("date", 0, Long.MAX_VALUE) && c.int("duration", 0, Long.MAX_VALUE)
            }
        },

        "SMS_RECEIVED" to TypeSpec(P, "sms.read") { p ->
            p.str("threadId", 32) && p.str("messageId", 32) && p.str("address", 64) && p.str("name", 128, optional = true) &&
                p.str("body", 10_000) && p.int("date", 0, Long.MAX_VALUE) && p.bool("outgoing", optional = true) && mmsParts(p)
        },
        "SMS_THREADS_GET" to TypeSpec(C, "sms.read") { p ->
            p.int("limit", 1, 100) && p.int("before", 0, Long.MAX_VALUE, optional = true) && p.str("query", 100, optional = true)
        },
        "SMS_THREADS" to TypeSpec(P, "sms.read") { p ->
            p.objArray("threads", 100) { t ->
                t.str("threadId", 32) && t.strArray("addresses", 64) && t.strArray("names", 128) && t.str("snippet", 200) &&
                    t.int("date", 0, Long.MAX_VALUE) && t.bool("unread")
            }
        },
        "SMS_MESSAGES_GET" to TypeSpec(C, "sms.read") { p ->
            p.str("threadId", 32) && p.int("limit", 1, 200) && p.int("before", 0, Long.MAX_VALUE, optional = true)
        },
        "SMS_MESSAGES" to TypeSpec(P, "sms.read") { p ->
            p.objArray("messages", 200) { m ->
                m.str("messageId", 32) && m.str("address", 64) && m.str("body", 10_000) && m.int("date", 0, Long.MAX_VALUE) &&
                    m.bool("outgoing") && m.bool("read") && (m.value("status") == null || m.oneOf("status", "sending", "failed")) &&
                    mmsParts(m)
            }
        },
        "SMS_SEND" to TypeSpec(C, "sms.send") { it.str("address", 64) && it.str("body", 5000) },
        "MMS_SEND" to TypeSpec(C, "sms.send") { p ->
            p.strArray("addresses", 64) && p.getJSONArray("addresses").length() in 1..20 && p.str("body", 5000)
        },
        "SMS_SEND_RESULT" to TypeSpec(P, "sms.send") { it.bool("ok") },
        "NOTIFICATION_POSTED" to TypeSpec(P, "notifications.read") { p ->
            p.str("key", 512) && p.str("package", 128) && p.str("appName", 128) && p.str("title", 500, optional = true) &&
                p.str("text", 4000, optional = true) && p.int("postedAt", 0, Long.MAX_VALUE) && p.bool("clearable") &&
                p.bool("existing", optional = true) &&
                p.objArray("actions", 5) { a -> a.int("index", 0, 63) && a.str("title", 64) && a.bool("reply") }
        },
        "NOTIFICATION_REMOVED" to TypeSpec(P, "notifications.read") { it.str("key", 512) },
        "NOTIFICATION_DISMISS" to TypeSpec(C, "notifications.act") { it.str("key", 512) },
        "NOTIFICATION_ACTION" to TypeSpec(C, "notifications.act") { p ->
            p.str("key", 512) && p.int("index", 0, 63) && p.str("replyText", 5000, optional = true)
        },
        "NOTIFICATION_RESULT" to TypeSpec(P, "notifications.act") { it.bool("ok") },
        "APP_ICON_GET" to TypeSpec(C, "notifications.read") { it.str("package", 128) },
        "APP_ICON" to TypeSpec(P, "notifications.read") { it.str("package", 128) && it.str("png", 90_000) },
        "PHOTOS_GET" to TypeSpec(C, "photos.read") {
            it.int("limit", 1, 200) && it.str("beforeId", 32, optional = true) && it.str("album", 32, optional = true)
        },
        "ALBUMS_GET" to TypeSpec(C, "photos.read", any),
        "ALBUMS" to TypeSpec(P, "photos.read") { p ->
            p.objArray("albums", 200) { a -> a.str("id", 32) && a.str("name", 255) && a.int("count", 0, Int.MAX_VALUE.toLong()) }
        },
        "CAMERA_REQUEST" to TypeSpec(C, "camera", any),
        "CAMERA_RESULT" to TypeSpec(P, "camera") { it.bool("ok") },
        "PHOTOS" to TypeSpec(P, "photos.read") { p ->
            p.objArray("photos", 200) { f ->
                f.str("id", 32) && f.str("name", 255, optional = true) && f.int("date", 0, Long.MAX_VALUE) &&
                    f.int("width", 0, 100_000) && f.int("height", 0, 100_000) && f.int("size", 0, Long.MAX_VALUE) &&
                    f.str("mime", 64) && f.bool("video", optional = true) && f.int("duration", 0, Long.MAX_VALUE, optional = true)
            }
        },
        "PHOTO_THUMB_GET" to TypeSpec(C, "photos.read") { it.str("id", 32) && it.bool("video", optional = true) },
        "PHOTO_THUMB" to TypeSpec(P, "photos.read") { it.str("id", 32) && it.str("jpeg", 200_000) },
        // Transfer connections: the PC's first frame instead of HELLO (docs/protocol.md "Transfer connections").
        // Authorization depends on the kind (photos.read, or an offer the phone made), so it's checked by the handler.
        "TRANSFER_PULL" to TypeSpec(C, null) { it.oneOf("kind", "photo", "video", "drop", "mms") && it.str("id", 64) },
        "TRANSFER_START" to TypeSpec(P, null) { it.int("size", 0, Long.MAX_VALUE) && it.str("name", 255) && it.str("mime", 127) },
        "TRANSFER_PUSH" to TypeSpec(C, "drop") { p ->
            p.oneOf("kind", "file", "clipboard") && p.str("name", 255) && p.int("size", 0, Long.MAX_VALUE) && p.str("mime", 127) &&
                p.str("folder", 255, optional = true)
        },
        "TRANSFER_DONE" to TypeSpec(P, "drop") { it.bool("ok") },
        "DROP_TEXT" to TypeSpec(C, "drop") { it.str("text", 50_000) },
        "DROP_DONE" to TypeSpec(P, "drop") { it.bool("ok") },
        "DROP_OFFER" to TypeSpec(P, "drop") { p ->
            p.str("dropId", 32) && p.str("text", 50_000, optional = true) && (p.value("purpose") == null || p.oneOf("purpose", "clipboard", "camera")) &&
                p.objArray("files", 50) { f -> f.int("index", 0, 49) && f.str("name", 255) && f.int("size", 0, Long.MAX_VALUE) && f.str("mime", 127) }
        },
        "DROP_RESULT" to TypeSpec(C, "drop") { it.str("dropId", 32) && it.bool("ok") },
        // Either way; the PC sends it only with sync turned on, the phone only when the user taps "Send clipboard".
        "CLIPBOARD_SET" to TypeSpec(null, "clipboard") { it.str("text", 50_000) },
        // The PC's Windows notifications, sent only while its setting is on. No reply.
        "PC_NOTIFICATION" to TypeSpec(C, "pc.notifications") { p ->
            p.str("id", 32) && p.str("app", 128) && p.str("title", 500, optional = true) && p.str("text", 4000, optional = true)
        },
        "PC_NOTIFICATION_REMOVED" to TypeSpec(C, "pc.notifications") { it.str("id", 32) },
        "DEVICE_STATUS" to TypeSpec(P, "device") { p ->
            p.bool("wifi") && p.int("wifiSignal", 0, 4, optional = true) && p.bool("bluetooth", optional = true)
                && p.int("storageFree", 0, Long.MAX_VALUE) && p.int("cellSignal", 0, 4, optional = true)
                && p.str("cellNetwork", 16, optional = true) && p.str("carrier", 64, optional = true)
        },
        "RING" to TypeSpec(C, "ring") { it.bool("on") },
        "RING_RESULT" to TypeSpec(P, "ring") { it.bool("ok") },
        "MEDIA_STATE" to TypeSpec(P, "media") { p ->
            p.bool("active") && p.str("app", 128, optional = true) && p.str("title", 500, optional = true)
                && p.str("artist", 500, optional = true) && p.bool("playing", optional = true)
        },
        "MEDIA_CONTROL" to TypeSpec(C, "media") { it.oneOf("action", "play", "pause", "next", "previous", "volumeUp", "volumeDown") },
        "MEDIA_RESULT" to TypeSpec(P, "media") { it.bool("ok") },
        "CONTACTS_GET" to TypeSpec(C, "contacts.read") { it.int("limit", 1, 500) && it.int("offset", 0, 1_000_000, optional = true) },
        "CONTACTS" to TypeSpec(P, "contacts.read") { p ->
            p.bool("more") && p.objArray("contacts", 500) { c ->
                c.str("id", 32) && c.str("name", 128) && c.strArray("emails", 254) && c.bool("photo", optional = true) &&
                    c.objArray("numbers", 20, ::contactNumber)
            }
        },
        "CONTACT_PHOTO_GET" to TypeSpec(C, "contacts.read") { it.str("id", 32) },
        "CONTACT_PHOTO" to TypeSpec(P, "contacts.read") { it.str("id", 32) && it.str("jpeg", 200_000, optional = true) },
        "CONTACT_SAVE" to TypeSpec(C, "contacts.write") { p ->
            p.str("id", 32, optional = true) && p.str("name", 128) && p.strArray("emails", 254) && p.objArray("numbers", 20, ::contactNumber)
        },
        "CONTACT_DELETE" to TypeSpec(C, "contacts.write") { it.str("id", 32) },
        "CONTACT_RESULT" to TypeSpec(P, "contacts.write") { it.bool("ok") && it.str("id", 32, optional = true) },

        // Phone as a remote for the PC. The PC says what it allows and what's playing; it re-checks its settings on every message.
        "PC_REMOTE" to TypeSpec(C, "remote") { p ->
            p.bool("input") && p.bool("media") && p.objArray("commands", 50) { it.str("id", 32) && it.str("name", 64) } &&
                p.str("app", 128, optional = true) && p.str("title", 500, optional = true) && p.str("artist", 500, optional = true) &&
                p.bool("playing", optional = true) && p.int("volume", 0, 100, optional = true) && p.bool("muted", optional = true)
        },
        "REMOTE_POINTER" to TypeSpec(P, "remote") { it.int("dx", -5000, 5000) && it.int("dy", -5000, 5000) },
        "REMOTE_BUTTON" to TypeSpec(P, "remote") { it.oneOf("button", "left", "right", "middle") && it.oneOf("action", "click", "down", "up") },
        "REMOTE_SCROLL" to TypeSpec(P, "remote") { it.int("dy", -12000, 12000) },
        "REMOTE_TEXT" to TypeSpec(P, "remote") { it.str("text", 1000) },
        "REMOTE_KEY" to TypeSpec(P, "remote") { it.oneOf("key", *REMOTE_KEYS) },
        "PC_MEDIA" to TypeSpec(P, "remote") { it.oneOf("action", "playPause", "next", "previous") },
        "PC_VOLUME" to TypeSpec(P, "remote") { it.int("level", 0, 100, optional = true) && it.bool("muted", optional = true) },
        "PC_COMMAND" to TypeSpec(P, "remote") { it.str("id", 32) },
        "PC_LOCK" to TypeSpec(P, "remote", any),
        "REMOTE_RESULT" to TypeSpec(C, "remote") { it.bool("ok") },

        // The phone's screen on the PC; frames go on their own connection (SCREEN_PULL, authorized by the handler).
        "SCREEN_REQUEST" to TypeSpec(C, "screen", any),
        "SCREEN_RESULT" to TypeSpec(P, "screen") { it.bool("ok") },
        "SCREEN_STATE" to TypeSpec(P, "screen") { it.oneOf("state", "started", "declined", "stopped") },
        "SCREEN_PULL" to TypeSpec(C, null, any),
        "SCREEN_TOUCH" to TypeSpec(C, "screen.control") { p ->
            p.int("x1", 0, 10_000) && p.int("y1", 0, 10_000) && p.int("x2", 0, 10_000) && p.int("y2", 0, 10_000) && p.int("ms", 1, 10_000)
        },
        "SCREEN_KEY" to TypeSpec(C, "screen.control") { it.oneOf("key", "back", "home", "recents", "backspace", "enter") },
        "SCREEN_TEXT" to TypeSpec(C, "screen.control") { it.str("text", 1000) },

        "PAIR_PROOF" to TypeSpec(P, null) { it.hex("mac", 64) },
        "PAIR_COMMIT" to TypeSpec(C, null) { it.hex("c", 64) },
        "PAIR_NONCE" to TypeSpec(P, null) { it.hex("n", 32) },
        "PAIR_REVEAL" to TypeSpec(C, null) { it.hex("n", 32) },
        "PAIR_CONFIRM" to TypeSpec(null, null) { it.bool("accepted") },
        "PAIR_RESULT" to TypeSpec(C, null) { it.bool("ok") && it.str("name", 64) },
    )
}

/** Keys the remote can press (REMOTE_KEY); letters and symbols go as REMOTE_TEXT. Must match the C# Catalog. */
val REMOTE_KEYS = arrayOf(
    "enter", "backspace", "delete", "tab", "escape", "left", "right", "up", "down", "home", "end", "pageUp", "pageDown", "f5",
)

/** MMS attachments on a message: absent for SMS. */
private fun mmsParts(m: JSONObject) = m.value("parts") == null ||
    m.objArray("parts", 10) { it.str("id", 32) && it.str("mime", 127) && it.str("name", 255, optional = true) }

private fun contactNumber(n: JSONObject) =
    n.str("number", 64) && n.oneOf("type", "mobile", "home", "work", "other") && n.str("label", 64, optional = true)

// org.json represents JSON null as JSONObject.NULL; treat it as absent.
private fun JSONObject.value(key: String): Any? = opt(key)?.takeIf { it != JSONObject.NULL }
private fun JSONObject.isInt(key: String) = value(key).let { it is Int || it is Long }
private fun JSONObject.str(key: String): String? = value(key) as? String

private fun JSONObject.str(key: String, max: Int, optional: Boolean = false): Boolean =
    when (val v = value(key)) {
        null -> optional
        is String -> v.length <= max
        else -> false
    }

private fun JSONObject.int(key: String, min: Long, max: Long, optional: Boolean = false): Boolean =
    when (val v = value(key)) {
        null -> optional
        is Int, is Long -> (v as Number).toLong() in min..max
        else -> false
    }

private fun JSONObject.bool(key: String, optional: Boolean = false) = value(key).let { if (it == null) optional else it is Boolean }
private fun JSONObject.oneOf(key: String, vararg values: String) = str(key) in values
private fun JSONObject.hex(key: String, length: Int) =
    str(key)?.let { s -> s.length == length && s.all { it in '0'..'9' || it in 'a'..'f' || it in 'A'..'F' } } == true

private fun JSONObject.strArray(key: String, maxLength: Int = 64): Boolean {
    val a = value(key) as? JSONArray ?: return false
    return a.length() <= 64 && (0 until a.length()).all { (a.opt(it) as? String)?.let { s -> s.length <= maxLength } == true }
}

private fun JSONObject.objArray(key: String, max: Int, item: (JSONObject) -> Boolean): Boolean {
    val a = value(key) as? JSONArray ?: return false
    return a.length() <= max && (0 until a.length()).all { (a.opt(it) as? JSONObject)?.let(item) == true }
}

fun ByteArray.hex(): String = joinToString("") { "%02x".format(it) }
fun String.unhex(): ByteArray = chunked(2).map { it.toInt(16).toByte() }.toByteArray()
