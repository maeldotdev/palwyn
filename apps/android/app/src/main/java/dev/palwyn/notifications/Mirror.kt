package dev.palwyn.notifications

import android.Manifest
import android.app.Notification
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.graphics.Canvas
import android.os.Bundle
import android.provider.Telephony
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import android.util.Base64
import android.util.Log
import dev.palwyn.link.LinkService
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream

/** Bound by Android once the user grants notification access; hands everything to [Mirror]. */
class MirrorListener : NotificationListenerService() {
    override fun onListenerConnected() {
        Mirror.listener = this
        Log.i(LinkService.TAG, "Notification access connected")
    }

    override fun onListenerDisconnected() {
        Mirror.listener = null
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) = Mirror.posted(this, sbn)
    override fun onNotificationRemoved(sbn: StatusBarNotification) = Mirror.removed(this, sbn)
}

/**
 * Mirrors the phone's notifications to the PC: posted, removed, dismiss, actions and replies
 * (Phase 0: NotificationListenerService, user-granted in Settings).
 *
 * Left out on purpose: ongoing ones (music, navigation, downloads, our own status), group summaries,
 * incoming-call notifications (the PC has its own call card) and the default SMS app's (the PC shows
 * those messages itself). Android 15 redacts one-time codes for apps like this one.
 */
object Mirror {
    const val TEXT_MAX = 4000

    @Volatile var listener: MirrorListener? = null

    private val _events = MutableSharedFlow<Pair<String, JSONObject>>(extraBufferCapacity = 128)
    /** (NOTIFICATION_POSTED | NOTIFICATION_REMOVED, payload) */
    val events: SharedFlow<Pair<String, JSONObject>> = _events

    /** Keys we mirrored, so a removal is only reported for notifications the PC knows about. */
    private val mirrored = java.util.concurrent.ConcurrentHashMap.newKeySet<String>()

    fun granted(c: Context): Boolean = c.getSystemService(NotificationManager::class.java)
        .isNotificationListenerAccessGranted(ComponentName(c, MirrorListener::class.java))

    fun posted(c: Context, sbn: StatusBarNotification) {
        if (!mirrors(c, sbn)) {
            // An update can turn a mirrored notification into one we skip (e.g. it became ongoing).
            if (mirrored.remove(sbn.key)) _events.tryEmit("NOTIFICATION_REMOVED" to JSONObject().put("key", sbn.key))
            return
        }
        mirrored += sbn.key
        _events.tryEmit("NOTIFICATION_POSTED" to payload(c, sbn, existing = false))
    }

    fun removed(c: Context, sbn: StatusBarNotification) {
        if (mirrored.remove(sbn.key)) _events.tryEmit("NOTIFICATION_REMOVED" to JSONObject().put("key", sbn.key))
    }

    /** What's in the phone's notification shade now, newest first, for a PC that just connected. */
    fun current(c: Context): List<JSONObject> {
        val l = listener ?: return emptyList()
        return try {
            l.activeNotifications.filter { mirrors(c, it) }.sortedByDescending { it.postTime }.take(50)
                .onEach { mirrored += it.key }.map { payload(c, it, existing = true) }
        } catch (e: RuntimeException) { // listener being unbound
            emptyList()
        }
    }

    private fun mirrors(c: Context, sbn: StatusBarNotification): Boolean {
        val n = sbn.notification
        if (sbn.packageName == c.packageName || sbn.isOngoing || sbn.key.length > 512) return false
        if (n.flags and Notification.FLAG_GROUP_SUMMARY != 0) return false
        if (n.category == Notification.CATEGORY_CALL) return false
        if (sbn.packageName == Telephony.Sms.getDefaultSmsPackage(c) &&
            c.checkSelfPermission(Manifest.permission.READ_SMS) == PackageManager.PERMISSION_GRANTED
        ) return false
        return title(n) != null || text(n) != null
    }

    private fun title(n: Notification) = n.extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.takeIf { it.isNotBlank() }
    private fun text(n: Notification) = (n.extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
        ?: n.extras.getCharSequence(Notification.EXTRA_TEXT))?.toString()?.takeIf { it.isNotBlank() }

    private fun payload(c: Context, sbn: StatusBarNotification, existing: Boolean): JSONObject {
        val n = sbn.notification
        val actions = JSONArray()
        n.actions?.forEachIndexed { i, a ->
            val label = a.title?.toString()?.takeIf { it.isNotBlank() } ?: return@forEachIndexed
            if (actions.length() < 5) actions.put(JSONObject()
                .put("index", i).put("title", label.take(64))
                .put("reply", a.remoteInputs?.any { it.allowFreeFormInput } == true))
        }
        return JSONObject()
            .put("key", sbn.key)
            .put("package", sbn.packageName.take(128))
            .put("appName", appName(c, sbn.packageName).take(128))
            .put("postedAt", sbn.postTime)
            .put("clearable", sbn.isClearable)
            .put("actions", actions)
            .apply {
                title(n)?.let { put("title", it.take(500)) }
                text(n)?.let { put("text", it.take(TEXT_MAX)) }
                if (existing) put("existing", true)
            }
    }

    // Launcher apps are visible through the manifest's <queries>; anything else shows its package name.
    fun appName(c: Context, pkg: String): String = try {
        c.packageManager.getApplicationLabel(c.packageManager.getApplicationInfo(pkg, 0)).toString()
    } catch (e: PackageManager.NameNotFoundException) {
        pkg
    }

    /** 64 px PNG of an app's icon, base64. */
    fun icon(c: Context, pkg: String): String? = try {
        val bitmap = Bitmap.createBitmap(64, 64, Bitmap.Config.ARGB_8888)
        c.packageManager.getApplicationIcon(pkg).apply { setBounds(0, 0, 64, 64) }.draw(Canvas(bitmap))
        val png = ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.PNG, 100, it) }.toByteArray()
        Base64.encodeToString(png, Base64.NO_WRAP)
    } catch (e: PackageManager.NameNotFoundException) {
        null
    }

    private fun find(key: String) = listener?.activeNotifications?.firstOrNull { it.key == key }

    /** Returns null on success, else a protocol error code. */
    fun dismiss(key: String): String? {
        val l = listener ?: return "PERMISSION_DENIED"
        val sbn = find(key) ?: return "FAILED"
        if (!sbn.isClearable) return "FAILED"
        l.cancelNotification(key)
        return null
    }

    /**
     * Fires action [index] of notification [key], with [replyText] for reply actions. Actions that open an
     * app screen may be blocked by Android's background-start limits; replies and "mark as read" (broadcasts)
     * are not.
     */
    fun act(c: Context, key: String, index: Int, replyText: String?): String? {
        if (listener == null) return "PERMISSION_DENIED"
        val action = find(key)?.notification?.actions?.getOrNull(index) ?: return "FAILED"
        return try {
            val inputs = action.remoteInputs?.filter { it.allowFreeFormInput }.orEmpty()
            if (inputs.isNotEmpty()) {
                if (replyText.isNullOrBlank()) return "FAILED"
                val intent = Intent()
                RemoteInput.addResultsToIntent(inputs.toTypedArray(), intent,
                    Bundle().apply { inputs.forEach { putCharSequence(it.resultKey, replyText) } })
                action.actionIntent.send(c, 0, intent)
            } else {
                action.actionIntent.send()
            }
            null
        } catch (e: PendingIntent.CanceledException) {
            "FAILED"
        }
    }
}
