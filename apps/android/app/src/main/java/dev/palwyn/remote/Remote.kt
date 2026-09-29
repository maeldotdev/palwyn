package dev.palwyn.remote

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.util.Log
import android.widget.Toast
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkService
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.IOException
import java.util.concurrent.ConcurrentHashMap

/** What a connected PC lets this phone do (its PC_REMOTE message), and what's playing there. */
data class PcRemote(
    val pcName: String,
    val input: Boolean,
    val media: Boolean,
    val commands: List<Pair<String, String>>, // id to name
    val app: String?, val title: String?, val artist: String?, val playing: Boolean?,
    val volume: Int?, val muted: Boolean?,
)

/**
 * This phone as a remote for a PC: pointer, clicks, scrolling, typing, keys, media, volume, lock and the PC's own
 * commands. Everything goes to [target], the PC picked on the Remote screen, and only while that screen is open.
 */
object Remote {
    private val _pcs = MutableStateFlow<Map<String, PcRemote>>(emptyMap())
    /** Connected PCs that sent PC_REMOTE, by certificate fingerprint (hex). */
    val pcs: StateFlow<Map<String, PcRemote>> = _pcs

    /** The PC the Remote screen controls; null while it's closed, which stops all sending. */
    @Volatile var target: String? = null

    /** Set while the Slides tab is open: the volume keys go back (false) and forward (true). */
    @Volatile var volumeKeys: ((forward: Boolean) -> Unit)? = null

    fun update(fingerprint: String, pcName: String, p: JSONObject) {
        val commands = p.getJSONArray("commands").let { a ->
            (0 until a.length()).map { a.getJSONObject(it).let { c -> c.getString("id") to c.getString("name") } }
        }
        fun str(key: String) = p.optString(key).takeIf { p.has(key) && it.isNotEmpty() }
        val state = PcRemote(
            pcName, p.getBoolean("input"), p.getBoolean("media"), commands,
            str("app"), str("title"), str("artist"), if (p.has("playing")) p.getBoolean("playing") else null,
            if (p.has("volume")) p.getInt("volume") else null, if (p.has("muted")) p.getBoolean("muted") else null,
        )
        _pcs.update { it + (fingerprint to state) }
    }

    fun remove(fingerprint: String) = _pcs.update { it - fingerprint }

    // ---- Sending: one thread, so messages keep their order; moves and scrolls are merged so a fast swipe
    // is about 60 messages a second, not one per touch sample.

    @OptIn(ExperimentalCoroutinesApi::class)
    private val scope = CoroutineScope(
        SupervisorJob() + Dispatchers.IO.limitedParallelism(1) +
            CoroutineExceptionHandler { _, e -> Log.i(LinkService.TAG, "Remote send failed: ${e.javaClass.simpleName}") }
    )
    private val lock = Any()
    private var dx = 0f
    private var dy = 0f
    private var wheel = 0f
    private var flushQueued = false
    private var volumeLevel: Int? = null
    private val pending = ConcurrentHashMap<String, String>() // request id → what it was, for error messages

    /** Pointer movement in PC pixels (fractions carry over). */
    fun move(x: Float, y: Float) = queue { dx += x; dy += y }

    /** Wheel units: 120 is one notch; negative scrolls down. */
    fun scroll(units: Float) = queue { wheel += units }

    fun button(button: String, action: String) = ordered("REMOTE_BUTTON", JSONObject().put("button", button).put("action", action))
    fun key(key: String) = ordered("REMOTE_KEY", JSONObject().put("key", key))
    fun text(text: String) {
        if (text.isNotEmpty()) ordered("REMOTE_TEXT", JSONObject().put("text", text.take(1000)))
    }

    /** Live typing from a text field: sends what changed between its old and new text. */
    fun typed(old: String, new: String) {
        val (backspaces, insert) = diff(old, new)
        repeat(backspaces.coerceAtMost(200)) { key("backspace") }
        text(insert)
    }

    fun media(action: String) = request("PC_MEDIA", JSONObject().put("action", action), "control the music")
    fun mute(muted: Boolean) = request("PC_VOLUME", JSONObject().put("muted", muted), "change the volume")
    /** [pc] defaults to the Remote screen's PC; Home passes one because nothing is targeted there. */
    fun lock(pc: String? = target) = request("PC_LOCK", JSONObject(), "lock your PC", pc)
    fun command(id: String, name: String) = request("PC_COMMAND", JSONObject().put("id", id), "run $name")

    /** Volume while a slider is dragged: only the latest level is sent, at most every 60 ms. */
    fun volume(level: Int) {
        val first = synchronized(lock) {
            val none = volumeLevel == null
            volumeLevel = level
            none
        }
        if (!first) return // a send is already queued; it picks up this level
        scope.launch {
            delay(60)
            val latest = synchronized(lock) { volumeLevel.also { volumeLevel = null } } ?: return@launch
            request("PC_VOLUME", JSONObject().put("level", latest), "change the volume")
        }
    }

    /** A reply from a PC: REMOTE_RESULT or ERROR. Failures of what the user tapped are shown. */
    fun replied(context: Context, replyTo: String, type: String, payload: JSONObject) {
        val what = pending.remove(replyTo) ?: return
        if (type != "ERROR") return
        val reason = when (payload.optString("code")) {
            "REMOTE_OFF" -> "It's turned off in Palwyn Settings on your PC."
            "NOTHING_PLAYING" -> "Nothing is playing on your PC."
            "NOT_FOUND" -> "That command was removed on your PC."
            "UNSUPPORTED_TYPE" -> "Update Palwyn on your PC."
            else -> "Your PC couldn't do it."
        }
        Handler(Looper.getMainLooper()).post { Toast.makeText(context, "Couldn't $what. $reason", Toast.LENGTH_SHORT).show() }
    }

    private inline fun queue(add: () -> Unit) {
        val schedule = synchronized(lock) {
            add()
            if (flushQueued) false else {
                flushQueued = true
                true
            }
        }
        if (schedule) scope.launch {
            delay(16)
            flush()
        }
    }

    /** Sends merged movement now; runs on [scope] before anything that must come after it. */
    private fun flush() {
        val (x, y, w) = synchronized(lock) {
            flushQueued = false
            val x = dx.toInt()
            val y = dy.toInt()
            val w = wheel.toInt()
            dx -= x
            dy -= y
            wheel -= w
            Triple(x, y, w)
        }
        if (x != 0 || y != 0) send("REMOTE_POINTER", JSONObject().put("dx", x.coerceIn(-5000, 5000)).put("dy", y.coerceIn(-5000, 5000)))
        if (w != 0) send("REMOTE_SCROLL", JSONObject().put("dy", w.coerceIn(-12000, 12000)))
    }

    private fun ordered(type: String, payload: JSONObject) {
        scope.launch {
            flush()
            send(type, payload)
        }
    }

    private fun request(type: String, payload: JSONObject, what: String, to: String? = target) {
        scope.launch { send(type, payload, to)?.let { pending[it] = what } }
    }

    private fun send(type: String, payload: JSONObject, to: String? = target): String? {
        val pc = to ?: return null
        return try {
            LinkServer.send(pc, type, payload)
        } catch (e: IOException) {
            null // the session is ending; the screen follows Remote.pcs
        }
    }

    /** Backspaces then text that turn [old] into [new], keeping their common start. */
    fun diff(old: String, new: String): Pair<Int, String> {
        val common = old.commonPrefixWith(new).length
        return (old.length - common) to new.substring(common)
    }

    /** Touchpad distance (dp) to PC pixels; Windows then applies its own pointer speed and acceleration. Tune here. */
    fun pixels(dp: Float): Float = dp * 1.6f
}
