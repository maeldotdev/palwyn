package dev.palwyn.device

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioManager
import android.media.Ringtone
import android.media.RingtoneManager
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.util.Log
import dev.palwyn.R
import dev.palwyn.link.LinkService

/**
 * "Ring my phone" from the PC: the phone's ringtone on the alarm stream at full volume, so it's heard in
 * silent and vibrate mode (Do Not Disturb still decides whether alarms sound). Stops from the PC, from the
 * notification, or after a minute; the alarm volume is put back.
 */
object Ringer {
    private const val CHANNEL = "ring"
    private const val NOTIFICATION = 3
    private const val MAX_MS = 60_000L

    private val main = Handler(Looper.getMainLooper())
    private var ringtone: Ringtone? = null
    private var savedVolume = -1

    fun ring(c: Context, on: Boolean) {
        main.post { if (on) start(c.applicationContext) else stop(c.applicationContext) }
    }

    private fun start(c: Context) {
        if (ringtone != null) return
        val audio = c.getSystemService(AudioManager::class.java)
        savedVolume = audio.getStreamVolume(AudioManager.STREAM_ALARM)
        try {
            audio.setStreamVolume(AudioManager.STREAM_ALARM, audio.getStreamMaxVolume(AudioManager.STREAM_ALARM), 0)
        } catch (e: SecurityException) { /* Do Not Disturb policy; ring at the current volume */ }

        val tone = RingtoneManager.getRingtone(c, Settings.System.DEFAULT_RINGTONE_URI)
            ?: RingtoneManager.getRingtone(c, Settings.System.DEFAULT_ALARM_ALERT_URI)
        ringtone = tone?.apply {
            audioAttributes = AudioAttributes.Builder()
                .setUsage(AudioAttributes.USAGE_ALARM)
                .setContentType(AudioAttributes.CONTENT_TYPE_SONIFICATION)
                .build()
            isLooping = true
            play()
        }

        val stop = PendingIntent.getBroadcast(c, 0, Intent(c, RingStopReceiver::class.java), PendingIntent.FLAG_IMMUTABLE)
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Ring from your PC", NotificationManager.IMPORTANCE_HIGH))
        nm.notify(NOTIFICATION, Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Your PC is ringing this phone")
            .setContentText("Tap to stop")
            .setContentIntent(stop)
            .setDeleteIntent(stop)
            .addAction(Notification.Action.Builder(null, "Stop", stop).build())
            .setOngoing(true)
            .build())
        main.postDelayed({ stop(c) }, MAX_MS)
        Log.i(LinkService.TAG, "Ringing (${if (tone == null) "no ringtone found" else "ringtone"})")
    }

    private fun stop(c: Context) {
        main.removeCallbacksAndMessages(null)
        val tone = ringtone ?: return
        ringtone = null
        tone.stop()
        if (savedVolume >= 0) try {
            c.getSystemService(AudioManager::class.java).setStreamVolume(AudioManager.STREAM_ALARM, savedVolume, 0)
        } catch (e: SecurityException) { }
        c.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)
        Log.i(LinkService.TAG, "Ringing stopped")
    }
}

class RingStopReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) = Ringer.ring(context, false)
}
