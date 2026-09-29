package dev.palwyn.pairing

import android.content.Context
import android.os.Handler
import android.os.Looper
import dev.palwyn.link.Advertiser
import dev.palwyn.protocol.hex
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

sealed interface PairingState {
    data object Idle : PairingState
    /** A QR invite arrived via deep link; the user must confirm before we accept anything. */
    data class Confirm(val invite: PairingInvite) : PairingState
    data class Waiting(val pcName: String?, val codeMode: Boolean) : PairingState
    data class ShowCode(val code: String) : PairingState
    data class Done(val pcName: String) : PairingState
    data class Failed(val message: String) : PairingState
}

/**
 * Which PC (if any) may connect for pairing right now, and the UI state. A pairing window lasts
 * 5 minutes and accepts one attempt.
 */
object PairingManager {
    private const val WINDOW_MS = 5 * 60 * 1000L
    private val main = Handler(Looper.getMainLooper())
    private val timeout = Runnable { fail(appContext ?: return@Runnable, "Pairing timed out. Try again.") }

    private val _state = MutableStateFlow<PairingState>(PairingState.Idle)
    val state: StateFlow<PairingState> = _state

    @Volatile private var invite: PairingInvite? = null
    @Volatile private var codeMode = false
    @Volatile private var busy = false
    private var answer: CompletableDeferred<Boolean>? = null
    private var appContext: Context? = null

    fun offer(invite: PairingInvite) {
        _state.value = PairingState.Confirm(invite)
    }

    fun acceptInvite(c: Context) {
        val offered = (_state.value as? PairingState.Confirm)?.invite ?: return
        open(c) { invite = offered }
        _state.value = PairingState.Waiting(offered.pcName, codeMode = false)
    }

    fun startCode(c: Context) {
        open(c) { codeMode = true }
        _state.value = PairingState.Waiting(null, codeMode = true)
    }

    fun cancel(c: Context) {
        answer?.complete(false)
        close(c)
        _state.value = PairingState.Idle
    }

    fun reset() {
        if (!isOpen()) _state.value = PairingState.Idle
    }

    fun answerCode(accepted: Boolean) {
        answer?.complete(accepted)
    }

    /** TLS trust decision for a client that is not already pinned. */
    fun acceptsForPairing(fingerprint: ByteArray): Boolean =
        !busy && (codeMode || invite?.pcFingerprint?.contentEquals(fingerprint) == true)

    /** "qr:<first 8 hex of the PC fingerprint>", "code", or null: the DNS-SD pair attribute. */
    val advertisedMode: String?
        get() = when {
            codeMode -> "code"
            invite != null -> "qr:" + invite!!.pcFingerprint.hex().take(8)
            else -> null
        }

    val currentInvite: PairingInvite? get() = invite
    val isCodeMode: Boolean get() = codeMode

    /** Claims the window for one connection so a second PC can't race the first. */
    @Synchronized
    fun claim(): Boolean = if (busy || !isOpen()) false else true.also { busy = true }

    fun showCode(code: String): CompletableDeferred<Boolean> =
        CompletableDeferred<Boolean>().also {
            answer = it
            _state.value = PairingState.ShowCode(code)
        }

    fun succeed(c: Context, pcName: String) {
        close(c)
        _state.value = PairingState.Done(pcName)
    }

    fun fail(c: Context, message: String) {
        close(c)
        _state.value = PairingState.Failed(message)
    }

    private fun isOpen() = invite != null || codeMode

    private fun open(c: Context, set: () -> Unit) {
        appContext = c.applicationContext
        invite = null
        codeMode = false
        busy = false
        set()
        main.removeCallbacks(timeout)
        main.postDelayed(timeout, WINDOW_MS)
        Advertiser.update(c)
    }

    private fun close(c: Context) {
        invite = null
        codeMode = false
        busy = false
        answer = null
        main.removeCallbacks(timeout)
        Advertiser.update(c)
    }
}
