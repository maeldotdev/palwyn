package dev.palwyn.calls

import android.Manifest
import android.annotation.SuppressLint
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.provider.CallLog
import android.telecom.TelecomManager
import android.telephony.TelephonyManager
import android.util.Log
import dev.palwyn.device.Contacts
import dev.palwyn.link.LinkService
import dev.palwyn.protocol.hex
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom

enum class CallState { RINGING, ACTIVE, ENDED }

data class Call(
    val id: String,
    val state: CallState,
    val incoming: Boolean,
    val number: String?,
    val name: String?,
    /** When this state began (Unix ms). */
    val since: Long,
) {
    fun toPayload(): JSONObject = JSONObject()
        .put("callId", id).put("state", state.name).put("direction", if (incoming) "IN" else "OUT").put("since", since)
        .apply {
            number?.let { put("number", it.take(64)) }
            name?.let { put("name", it.take(128)) }
        }
}

/**
 * The phone's cellular call, from the PHONE_STATE broadcast (verified on the reference phone in Phase 0), and
 * control through TelecomManager. PHONE_STATE carries the number only with READ_CALL_LOG, and usually in a
 * second broadcast, so a call is first reported without it.
 *
 * VoIP calls (WhatsApp, Messenger, ...) are not telephony calls and never appear here.
 */
object Calls {
    private val _current = MutableStateFlow<Call?>(null)
    /** The current or most recent call; equal values aren't re-emitted, which absorbs duplicate broadcasts. */
    val current: StateFlow<Call?> = _current

    private var receiver: BroadcastReceiver? = null
    private lateinit var app: Context
    private val random = SecureRandom()

    fun start(c: Context) {
        if (receiver != null) return
        app = c.applicationContext
        // Registered while the link service runs; delivery is permission-checked, so granting later just works.
        receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = onPhoneState(intent)
        }.also { app.registerReceiver(it, IntentFilter(TelephonyManager.ACTION_PHONE_STATE_CHANGED)) }
    }

    fun stop(c: Context) {
        receiver?.let { c.applicationContext.unregisterReceiver(it) }
        receiver = null
    }

    // ponytail: PHONE_STATE is one state for the whole phone, so a waiting second call replaces the first and
    // "answered the waiting call" can't be told from "declined it". Tracking calls separately needs an
    // InCallService, which only the default dialer gets (Phase 0 section 2).
    @Synchronized
    private fun onPhoneState(intent: Intent) {
        val state = intent.getStringExtra(TelephonyManager.EXTRA_STATE) ?: return
        @Suppress("DEPRECATION")
        val number = intent.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER)?.takeIf { it.isNotBlank() }
        val now = System.currentTimeMillis()
        val cur = _current.value?.takeIf { it.state != CallState.ENDED }
        val next = when (state) {
            TelephonyManager.EXTRA_STATE_RINGING ->
                if (cur?.state == CallState.RINGING) cur.withNumber(number)
                else Call(newId(), CallState.RINGING, true, number, Contacts.name(app, number), now)
            TelephonyManager.EXTRA_STATE_OFFHOOK -> when {
                cur == null -> Call(newId(), CallState.ACTIVE, false, number, Contacts.name(app, number), now)
                cur.state == CallState.RINGING -> cur.copy(state = CallState.ACTIVE, since = now).withNumber(number)
                else -> cur.withNumber(number)
            }
            TelephonyManager.EXTRA_STATE_IDLE -> cur?.copy(state = CallState.ENDED, since = now) ?: return
            else -> return
        }
        if (next != _current.value) Log.i(LinkService.TAG, "Call ${next.state} ${if (next.incoming) "in" else "out"} ${mask(next.number)}")
        _current.value = next
    }

    private fun Call.withNumber(n: String?) = if (n == null || number != null) this else copy(number = n, name = Contacts.name(app, n))

    private fun newId() = ByteArray(6).also(random::nextBytes).hex()

    /** Logs never carry full numbers (docs/architecture.md section 7). */
    fun mask(number: String?) = number?.let { "••" + it.takeLast(2) } ?: "(no number)"

    private fun granted(permission: String) = app.checkSelfPermission(permission) == PackageManager.PERMISSION_GRANTED

    /**
     * CALL_ANSWER / CALL_DECLINE / CALL_END for [callId]. Returns null when Telecom accepted it, else a protocol
     * error code. acceptRingingCall/endCall are deprecated since Android 10 but still work for non-dialer apps
     * holding ANSWER_PHONE_CALLS (Phase 0); endCall refuses emergency calls, as it should.
     */
    @SuppressLint("MissingPermission")
    @Suppress("DEPRECATION")
    fun command(type: String, callId: String): String? {
        if (!granted(Manifest.permission.ANSWER_PHONE_CALLS)) return "PERMISSION_DENIED"
        val cur = _current.value
        val expected = if (type == "CALL_END") CallState.ACTIVE else CallState.RINGING
        if (cur == null || cur.id != callId || cur.state != expected) return "FAILED"
        val telecom = app.getSystemService(TelecomManager::class.java)
        return try {
            when (type) {
                "CALL_ANSWER" -> {
                    telecom.acceptRingingCall()
                    null
                }
                else -> if (telecom.endCall()) null else "FAILED"
            }
        } catch (e: SecurityException) {
            "PERMISSION_DENIED"
        }
    }

    /**
     * Newest first, at most [limit], older than [before] (Unix ms) when given, only calls whose contact name or
     * number matches [query] when given. Names come from the contacts now, so an edit shows at once (the call
     * log's cached name is updated only later, by the dialer). Needs READ_CALL_LOG.
     */
    fun history(limit: Int, before: Long?, query: String? = null): JSONArray {
        val out = JSONArray()
        val names = HashMap<String, String?>()
        // A search has to look past the first [limit] rows; the call log keeps a few hundred at most.
        val uri = if (query != null) CallLog.Calls.CONTENT_URI
            else CallLog.Calls.CONTENT_URI.buildUpon().appendQueryParameter(CallLog.Calls.LIMIT_PARAM_KEY, "$limit").build()
        app.contentResolver.query(
            uri,
            arrayOf(CallLog.Calls._ID, CallLog.Calls.NUMBER, CallLog.Calls.CACHED_NAME, CallLog.Calls.TYPE, CallLog.Calls.DATE, CallLog.Calls.DURATION),
            before?.let { "${CallLog.Calls.DATE} < ?" }, before?.let { arrayOf("$it") },
            "${CallLog.Calls.DATE} DESC",
        )?.use { c ->
            while (c.moveToNext() && out.length() < limit) {
                val type = when (c.getInt(3)) {
                    CallLog.Calls.INCOMING_TYPE, CallLog.Calls.ANSWERED_EXTERNALLY_TYPE -> "IN"
                    CallLog.Calls.OUTGOING_TYPE -> "OUT"
                    CallLog.Calls.MISSED_TYPE -> "MISSED"
                    CallLog.Calls.REJECTED_TYPE -> "REJECTED"
                    CallLog.Calls.BLOCKED_TYPE -> "BLOCKED"
                    CallLog.Calls.VOICEMAIL_TYPE -> "VOICEMAIL"
                    else -> continue
                }
                val number = c.getString(1)?.takeIf { it.isNotBlank() }
                val name = (number?.let { n -> names.getOrPut(n) { Contacts.name(app, n) } } ?: c.getString(2))?.takeIf { it.isNotBlank() }
                if (query != null && !Contacts.matches(query, name, listOfNotNull(number))) continue
                out.put(JSONObject()
                    .put("id", c.getLong(0).toString())
                    .put("type", type)
                    .put("date", c.getLong(4))
                    .put("duration", c.getLong(5).coerceAtLeast(0))
                    .apply {
                        number?.let { put("number", it.take(64)) }
                        name?.let { put("name", it.take(128)) }
                    })
            }
        }
        return out
    }
}
