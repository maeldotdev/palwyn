package dev.palwyn.media

import android.content.ComponentName
import android.content.Context
import android.media.AudioManager
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.view.KeyEvent
import dev.palwyn.notifications.Mirror
import dev.palwyn.notifications.MirrorListener
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** What's playing on the phone; null = nothing. */
data class NowPlaying(val app: String, val title: String?, val artist: String?, val playing: Boolean)

/**
 * Media sessions of other apps (MEDIA_STATE / MEDIA_CONTROL). Android shows them only to an enabled
 * notification listener, so this needs the same access as mirroring. Event-driven, no polling.
 */
object Media {
    private val _now = MutableStateFlow<NowPlaying?>(null)
    val now: StateFlow<NowPlaying?> = _now

    private val main = Handler(Looper.getMainLooper())
    private var app: Context? = null
    private var sessions: MediaSessionManager? = null
    private var controller: MediaController? = null

    private val changed = MediaSessionManager.OnActiveSessionsChangedListener { pick(it.orEmpty()) }
    private val callback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) = publish()
        override fun onMetadataChanged(metadata: MediaMetadata?) = publish()
        override fun onSessionDestroyed() = pick(sessions?.getActiveSessions(component()).orEmpty())
    }

    /** Safe to call again, e.g. after the user grants notification access. */
    fun start(c: Context) = main.post {
        if (sessions != null || !Mirror.granted(c)) return@post
        app = c.applicationContext
        val msm = c.getSystemService(MediaSessionManager::class.java)
        try {
            msm.addOnActiveSessionsChangedListener(changed, component(), main)
            sessions = msm
            pick(msm.getActiveSessions(component()))
        } catch (e: SecurityException) { /* access was just revoked */ }
    }

    fun stop() = main.post {
        sessions?.removeOnActiveSessionsChangedListener(changed)
        controller?.unregisterCallback(callback)
        sessions = null
        controller = null
        _now.value = null
    }

    /** Returns an error code, or null when done. */
    fun control(c: Context, action: String): String? {
        val audio = c.getSystemService(AudioManager::class.java)
        when (action) {
            "volumeUp" -> audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_RAISE, 0)
            "volumeDown" -> audio.adjustStreamVolume(AudioManager.STREAM_MUSIC, AudioManager.ADJUST_LOWER, 0)
            else -> {
                val controls = controller?.transportControls
                if (controls != null) when (action) {
                    "play" -> controls.play()
                    "pause" -> controls.pause()
                    "next" -> controls.skipToNext()
                    "previous" -> controls.skipToPrevious()
                } else {
                    // No session: a media key resumes whatever played last, like a headset button.
                    val key = when (action) {
                        "play" -> KeyEvent.KEYCODE_MEDIA_PLAY
                        "pause" -> KeyEvent.KEYCODE_MEDIA_PAUSE
                        "next" -> KeyEvent.KEYCODE_MEDIA_NEXT
                        else -> KeyEvent.KEYCODE_MEDIA_PREVIOUS
                    }
                    val t = SystemClock.uptimeMillis()
                    audio.dispatchMediaKeyEvent(KeyEvent(t, t, KeyEvent.ACTION_DOWN, key, 0))
                    audio.dispatchMediaKeyEvent(KeyEvent(t, t, KeyEvent.ACTION_UP, key, 0))
                }
            }
        }
        return null
    }

    private fun component() = ComponentName(app!!, MirrorListener::class.java)

    // Prefer a session that's playing; Android lists the most recently active first.
    private fun pick(list: List<MediaController>) {
        val next = list.firstOrNull { it.playbackState?.isPlaying() == true } ?: list.firstOrNull()
        if (next?.sessionToken != controller?.sessionToken) {
            controller?.unregisterCallback(callback)
            controller = next
            next?.registerCallback(callback, main)
        }
        publish()
    }

    private fun publish() {
        val c = controller
        val meta = c?.metadata
        val title = meta?.getString(MediaMetadata.METADATA_KEY_TITLE) ?: meta?.getString(MediaMetadata.METADATA_KEY_DISPLAY_TITLE)
        _now.value = if (c == null || (title == null && c.playbackState?.isPlaying() != true)) null else NowPlaying(
            app = Mirror.appName(app!!, c.packageName).take(128),
            title = title?.take(500),
            artist = (meta?.getString(MediaMetadata.METADATA_KEY_ARTIST) ?: meta?.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST))?.take(500),
            playing = c.playbackState?.isPlaying() == true,
        )
    }

    private fun PlaybackState.isPlaying() = state == PlaybackState.STATE_PLAYING || state == PlaybackState.STATE_BUFFERING
}
