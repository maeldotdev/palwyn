// Phase 0 spike. Throwaway: plain TCP + token, reports counts/states only, never content.
@file:Suppress("DEPRECATION")

package dev.palwyn.spike

import android.annotation.SuppressLint
import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.app.role.RoleManager
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import android.provider.MediaStore
import android.provider.Settings
import android.provider.Telephony
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.telecom.Call
import android.telecom.CallScreeningService
import android.telecom.TelecomManager
import android.telephony.TelephonyManager
import android.util.Log
import android.util.Size
import android.widget.ScrollView
import android.widget.TextView
import java.io.PrintWriter
import java.net.ServerSocket
import java.net.Socket
import java.security.SecureRandom
import java.util.concurrent.CopyOnWriteArrayList
import kotlin.concurrent.thread

const val PORT = 47800

object Spike {
    val token: String = ByteArray(6).also { SecureRandom().nextBytes(it) }.joinToString("") { "%02x".format(it) }
    private val clients = CopyOnWriteArrayList<PrintWriter>()

    fun event(s: String) {
        Log.i("PBSpike", s)
        val line = "EVT t=${System.currentTimeMillis()} $s"
        clients.forEach { w -> runCatching { synchronized(w) { w.println(line); w.flush() } } }
    }

    fun add(w: PrintWriter) = clients.add(w)
    fun remove(w: PrintWriter) = clients.remove(w)
    fun mask(n: String?) = when {
        n == null -> "null"
        n.isEmpty() -> "empty"
        else -> "…" + n.takeLast(2)
    }
}

class MainActivity : Activity() {
    private lateinit var tv: TextView

    private val perms = buildList {
        addAll(listOf(
            "android.permission.READ_PHONE_STATE", "android.permission.READ_CALL_LOG",
            "android.permission.READ_CONTACTS", "android.permission.ANSWER_PHONE_CALLS",
            "android.permission.READ_SMS", "android.permission.RECEIVE_SMS",
        ))
        if (Build.VERSION.SDK_INT >= 33) addAll(listOf(
            "android.permission.READ_MEDIA_IMAGES", "android.permission.READ_MEDIA_VIDEO",
            "android.permission.POST_NOTIFICATIONS",
        )) else add("android.permission.READ_EXTERNAL_STORAGE")
    }.toTypedArray()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        tv = TextView(this).apply { setPadding(40, 120, 40, 40); textSize = 14f }
        setContentView(ScrollView(this).apply { addView(tv) })
        requestPermissions(perms, 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        startForegroundService(Intent(this, LinkService::class.java))
        val rm = getSystemService(RoleManager::class.java)
        if (!rm.isRoleHeld(RoleManager.ROLE_CALL_SCREENING)) {
            startActivityForResult(rm.createRequestRoleIntent(RoleManager.ROLE_CALL_SCREENING), 2)
        } else openListenerSettingsIfNeeded()
        render()
    }

    @Deprecated("spike")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        Spike.event("ROLE_CALL_SCREENING result=$resultCode")
        openListenerSettingsIfNeeded()
    }

    private fun openListenerSettingsIfNeeded() {
        if (!listenerGranted()) startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
    }

    private fun listenerGranted() = getSystemService(NotificationManager::class.java)
        .isNotificationListenerAccessGranted(ComponentName(this, NotifListener::class.java))

    override fun onResume() {
        super.onResume()
        render()
    }

    private fun render() {
        val granted = perms.joinToString("\n") {
            "${it.substringAfterLast('.')}: ${if (checkSelfPermission(it) == 0) "granted" else "DENIED"}"
        }
        tv.text = """
            Palwyn Phase 0 spike
            Port: $PORT   token: ${Spike.token}

            $granted
            Call screening role: ${getSystemService(RoleManager::class.java).isRoleHeld(RoleManager.ROLE_CALL_SCREENING)}
            Notification access: ${listenerGranted()}
        """.trimIndent()
        Log.i("PBSpike", "READY port=$PORT token=${Spike.token}")
    }
}

class LinkService : Service() {
    private val started = SystemClock.elapsedRealtime()

    private val phoneState = object : BroadcastReceiver() {
        override fun onReceive(c: Context, i: Intent) {
            Spike.event("PHONE_STATE state=${i.getStringExtra(TelephonyManager.EXTRA_STATE)} " +
                "number=${Spike.mask(i.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER))}")
        }
    }

    override fun onBind(intent: Intent?) = null

    override fun onCreate() {
        super.onCreate()
        val nm = getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel("link", "Link", NotificationManager.IMPORTANCE_LOW))
        val n = Notification.Builder(this, "link")
            .setContentTitle("Palwyn spike")
            .setContentText("Listening on port $PORT")
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth)
            .build()
        startForeground(1, n, ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        val filter = IntentFilter(TelephonyManager.ACTION_PHONE_STATE_CHANGED)
        if (Build.VERSION.SDK_INT >= 33) registerReceiver(phoneState, filter, Context.RECEIVER_EXPORTED)
        else registerReceiver(phoneState, filter)
        thread(name = "pb-server") { serve() }
        Spike.event("SERVICE started")
    }

    override fun onDestroy() {
        Spike.event("SERVICE destroyed")
        unregisterReceiver(phoneState)
        super.onDestroy()
    }

    private fun serve() = runCatching {
        ServerSocket(PORT).use { ss -> while (true) { val s = ss.accept(); thread { client(s) } } }
    }.onFailure { Spike.event("SERVER failed ${it.javaClass.simpleName}: ${it.message}") }

    private fun client(s: Socket) = s.use {
        val r = s.getInputStream().bufferedReader()
        val w = PrintWriter(s.getOutputStream(), true)
        if (r.readLine()?.trim() != Spike.token) { w.println("DENIED"); return }
        Spike.add(w)
        Spike.event("CLIENT connected from ${s.inetAddress.hostAddress}")
        try {
            while (true) {
                val line = r.readLine() ?: break
                val reply = runCatching { command(line.trim()) }
                    .getOrElse { "ERR ${it.javaClass.simpleName}: ${it.message}" }
                synchronized(w) { w.println("RES $reply") }
            }
        } finally {
            Spike.remove(w)
            Spike.event("CLIENT disconnected")
        }
    }

    @SuppressLint("MissingPermission")
    private fun command(cmd: String): String {
        val tm = getSystemService(TelecomManager::class.java)
        return when (cmd.substringBefore(' ').uppercase()) {
            "STATUS" -> {
                val bm = getSystemService(BatteryManager::class.java)
                "inCall=${tm.isInCall} battery=${bm.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)} " +
                    "charging=${bm.isCharging} uptimeS=${(SystemClock.elapsedRealtime() - started) / 1000} " +
                    "model=${Build.MODEL} sdk=${Build.VERSION.SDK_INT}"
            }
            "ANSWER" -> { tm.acceptRingingCall(); "ANSWER sent inCall=${tm.isInCall}" }
            "END" -> "END result=${tm.endCall()}"
            "SMS" -> {
                val msgs = count(Telephony.Sms.CONTENT_URI)
                val threads = count(Telephony.Threads.CONTENT_URI.buildUpon().appendQueryParameter("simple", "true").build())
                "SMS messages=$msgs threads=$threads"
            }
            "PHOTOS" -> {
                val uri = MediaStore.Images.Media.EXTERNAL_CONTENT_URI
                val total = count(uri)
                val albums = contentResolver.query(uri, arrayOf(MediaStore.Images.Media.BUCKET_ID), null, null, null)
                    ?.use { c -> buildSet { while (c.moveToNext()) add(c.getString(0)) }.size }
                val newest = contentResolver.query(uri, arrayOf(MediaStore.Images.Media._ID), null, null,
                    "${MediaStore.Images.Media.DATE_ADDED} DESC")?.use { c -> if (c.moveToFirst()) c.getLong(0) else null }
                val thumb = newest?.let {
                    val t0 = SystemClock.elapsedRealtime()
                    val bmp = contentResolver.loadThumbnail(
                        android.content.ContentUris.withAppendedId(uri, it), Size(256, 256), null)
                    "${bmp.width}x${bmp.height} in ${SystemClock.elapsedRealtime() - t0}ms"
                }
                "PHOTOS images=$total albums=$albums videos=${count(MediaStore.Video.Media.EXTERNAL_CONTENT_URI)} thumb=$thumb"
            }
            "NOTIF" -> {
                val active = NotifListener.instance?.activeNotifications
                "NOTIF listenerBound=${NotifListener.instance != null} active=${active?.size} " +
                    "apps=${active?.map { it.packageName }?.toSet()?.size} withReplyAction=${active?.count { sbn ->
                        sbn.notification.actions?.any { a -> a.remoteInputs?.isNotEmpty() == true } == true }}"
            }
            "MEDIA" -> {
                val sessions = getSystemService(MediaSessionManager::class.java)
                    .getActiveSessions(ComponentName(this, NotifListener::class.java))
                "MEDIA sessions=${sessions.size} playing=${sessions.count { it.playbackState?.state == PlaybackState.STATE_PLAYING }}"
            }
            "CLIP" -> {
                val text = cmd.substringAfter(' ', "")
                val done = java.util.concurrent.CountDownLatch(1)
                var result = "?"
                Handler(Looper.getMainLooper()).post {
                    result = runCatching {
                        getSystemService(ClipboardManager::class.java).setPrimaryClip(ClipData.newPlainText("pb", text)); "ok"
                    }.getOrElse { "${it.javaClass.simpleName}: ${it.message}" }
                    done.countDown()
                }
                done.await(3, java.util.concurrent.TimeUnit.SECONDS)
                "CLIP write=$result"
            }
            else -> "UNKNOWN (STATUS|ANSWER|END|SMS|PHOTOS|NOTIF|MEDIA|CLIP <text>)"
        }
    }

    private fun count(uri: android.net.Uri) =
        contentResolver.query(uri, arrayOf("_id"), null, null, null)?.use { it.count }
}

class Screening : CallScreeningService() {
    override fun onScreenCall(details: Call.Details) {
        Spike.event("SCREEN direction=${details.callDirection} handle=${Spike.mask(details.handle?.schemeSpecificPart)}")
        respondToCall(details, CallResponse.Builder().build())
    }
}

class NotifListener : NotificationListenerService() {
    companion object { @Volatile var instance: NotifListener? = null }

    override fun onListenerConnected() { instance = this; Spike.event("NOTIF_LISTENER connected") }
    override fun onListenerDisconnected() { instance = null; Spike.event("NOTIF_LISTENER disconnected") }
    override fun onNotificationPosted(sbn: StatusBarNotification) {
        if (sbn.packageName != packageName) Spike.event("NOTIF_POSTED app=${sbn.packageName}")
    }
}
