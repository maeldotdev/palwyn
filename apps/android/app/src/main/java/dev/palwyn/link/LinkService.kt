package dev.palwyn.link

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.wifi.WifiManager
import android.os.PowerManager
import android.util.Log
import dev.palwyn.Prefs
import dev.palwyn.R
import dev.palwyn.calls.Calls
import dev.palwyn.clipboard.ClipboardSync
import dev.palwyn.device.PhoneMonitor
import dev.palwyn.media.Media
import dev.palwyn.messages.Messages
import dev.palwyn.ui.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

enum class LinkState { NotPaired, Disconnected, Connecting, Connected }

object Link {
    private val _state = MutableStateFlow(LinkState.NotPaired)
    val state: StateFlow<LinkState> = _state
    private val _pcName = MutableStateFlow<String?>(null)
    /** The connected PC, or the most recently paired one. */
    val pcName: StateFlow<String?> = _pcName

    fun refresh(c: Context) {
        val paired = PairedPcs.all(c)
        _state.value = when {
            LinkServer.hasSessions() -> LinkState.Connected
            paired.isEmpty() -> LinkState.NotPaired
            else -> LinkState.Disconnected
        }
        _pcName.value = LinkServer.connectedNames().firstOrNull() ?: paired.maxByOrNull { it.pairedAt }?.name
        KeepAwake.set(c, _state.value == LinkState.Connected)
        Advertiser.update(c)
        c.getSystemService(NotificationManager::class.java)
            .notify(Notifications.STATUS_ID, Notifications.status(c, statusText(), _state.value == LinkState.Connected))
    }

    fun statusText(): String = when (_state.value) {
        LinkState.Connected -> "Connected to ${_pcName.value}"
        LinkState.Disconnected -> "Waiting for ${_pcName.value ?: "your PC"}"
        else -> "Not paired with a PC yet"
    }
}

/**
 * Held while a PC is connected. With the screen off Android lets Wi-Fi drop into power save and the CPU
 * suspend; the PC's pings then go unanswered and the link drops after a few idle minutes (seen on the
 * reference phone). A partial wake lock and a high-performance Wi-Fi lock keep both awake. Battery
 * optimization exemption is what lets the wake lock work in Doze. Released as soon as no PC is connected.
 */
object KeepAwake {
    private var wake: PowerManager.WakeLock? = null
    private var wifi: WifiManager.WifiLock? = null

    @Synchronized
    fun set(c: Context, on: Boolean) {
        if (on == (wake != null)) return
        if (on) {
            val app = c.applicationContext
            wake = app.getSystemService(PowerManager::class.java)
                .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Palwyn:link").apply { setReferenceCounted(false); acquire() }
            @Suppress("DEPRECATION") // HIGH_PERF is deprecated on 14+ but still keeps Wi-Fi out of power save there
            wifi = app.getSystemService(WifiManager::class.java)
                .createWifiLock(WifiManager.WIFI_MODE_FULL_HIGH_PERF, "Palwyn:link").apply { setReferenceCounted(false); acquire() }
            Log.i(LinkService.TAG, "Keeping CPU and Wi-Fi awake while connected")
        } else {
            wake?.release()
            wifi?.release()
            wake = null
            wifi = null
            Log.i(LinkService.TAG, "Released CPU and Wi-Fi locks")
        }
    }
}

/**
 * Keeps Palwyn alive in the background so a paired PC can reach this phone. Type
 * connectedDevice: no runtime cap (unlike dataSync) and allowed to start from BOOT_COMPLETED.
 */
class LinkService : Service() {
    override fun onBind(intent: Intent?) = null

    override fun onCreate() {
        super.onCreate()
        startForeground(
            Notifications.STATUS_ID,
            Notifications.status(this, Link.statusText()),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE,
        )
        PhoneMonitor.start(this)
        Calls.start(this)
        Messages.start(this)
        Media.start(this)
        LinkServer.start(this)
        Link.refresh(this)
        scope.launch {
            PhoneMonitor.snapshot.filterNotNull().map { it.onWifi }.distinctUntilChanged().drop(1).collect { onWifi ->
                Log.i(TAG, if (onWifi) "Wi-Fi available" else "Wi-Fi lost")
                if (onWifi) Advertiser.restart(this@LinkService)
            }
        }
        Log.i(TAG, "Link service started")
    }

    private val scope = CoroutineScope(Dispatchers.Default)

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int) = START_STICKY

    override fun onDestroy() {
        scope.cancel()
        KeepAwake.set(this, false)
        LinkServer.stop()
        Advertiser.stop(this)
        PhoneMonitor.stop(this)
        Calls.stop(this)
        Messages.stop(this)
        Media.stop()
        Log.i(TAG, "Link service stopped")
        super.onDestroy()
    }

    companion object {
        const val TAG = "Palwyn"
        fun start(c: Context) {
            c.startForegroundService(Intent(c, LinkService::class.java))
        }
    }
}

class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (!Prefs.autoStart(context)) return
        try {
            LinkService.start(context)
        } catch (e: IllegalStateException) {
            // ForegroundServiceStartNotAllowedException extends IllegalStateException.
            Log.w(LinkService.TAG, "Could not start link service after ${intent.action}", e)
        }
    }
}

object Notifications {
    const val CHANNEL = "link"
    const val STATUS_ID = 1

    fun createChannel(c: Context) {
        val channel = NotificationChannel(CHANNEL, "Connection status", NotificationManager.IMPORTANCE_LOW).apply {
            description = "Shows that Palwyn is running so your PC can reach this phone."
            setShowBadge(false)
        }
        c.getSystemService(NotificationManager::class.java).createNotificationChannel(channel)
    }

    /** The ongoing notification; while connected it also offers "Send clipboard" (a tap, as Android requires). */
    fun status(c: Context, text: String, connected: Boolean = false): Notification = Notification.Builder(c, CHANNEL)
        .setSmallIcon(R.drawable.ic_notification)
        .setContentTitle("Palwyn")
        .setContentText(text)
        .setOngoing(true)
        .setCategory(Notification.CATEGORY_SERVICE)
        .setContentIntent(
            PendingIntent.getActivity(c, 0, Intent(c, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        )
        .apply {
            if (connected && Prefs.clipboard(c)) addAction(Notification.Action.Builder(null, "Send clipboard to PC", ClipboardSync.pendingIntent(c)).build())
        }
        .build()
}
