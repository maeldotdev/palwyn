package dev.palwyn.notifications

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import dev.palwyn.R
import org.json.JSONObject
import java.util.concurrent.ConcurrentHashMap

/**
 * The connected PC's Windows notifications, shown on the phone (PC_NOTIFICATION). The PC sends them only
 * while its setting is on. They are removed when the PC's copy goes, and all of them when the session ends,
 * since the phone can't tell what changed on the PC while disconnected. Mirror skips our own notifications,
 * so these never echo back to the PC.
 */
object PcNotifications {
    private const val CHANNEL = "pc"
    private const val TAG = "pc:"
    private val shown = ConcurrentHashMap.newKeySet<String>()

    fun enabled(c: Context) = c.getSystemService(NotificationManager::class.java).areNotificationsEnabled()

    fun show(c: Context, p: JSONObject) {
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Notifications from your PC", NotificationManager.IMPORTANCE_DEFAULT))
        val id = p.getString("id")
        val app = p.getString("app")
        val text = p.optString("text")
        nm.notify(TAG + id, 0, Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(p.optString("title").ifBlank { app })
            .setContentText(text)
            .setSubText(app)
            .setStyle(Notification.BigTextStyle().bigText(text))
            .setWhen(System.currentTimeMillis())
            .setShowWhen(true)
            .build())
        shown += id
    }

    fun remove(c: Context, id: String) {
        c.getSystemService(NotificationManager::class.java).cancel(TAG + id, 0)
        shown -= id
    }

    fun clear(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        shown.forEach { nm.cancel(TAG + it, 0) }
        shown.clear()
    }
}
