package dev.palwyn

import android.app.Application
import android.content.Context
import dev.palwyn.device.PhoneMonitor
import dev.palwyn.link.Notifications

class PalwynApp : Application() {
    override fun onCreate() {
        super.onCreate()
        Notifications.createChannel(this)
        PhoneMonitor.init(this)
    }
}

object Prefs {
    private fun prefs(c: Context) = c.getSharedPreferences("settings", Context.MODE_PRIVATE)

    /** Start the link service after the phone restarts. */
    fun autoStart(c: Context) = prefs(c).getBoolean("autoStart", true)
    fun setAutoStart(c: Context, on: Boolean) = prefs(c).edit().putBoolean("autoStart", on).apply()

    /** Share the clipboard with the PC, both ways. Off drops the "clipboard" capability. */
    fun clipboard(c: Context) = prefs(c).getBoolean("clipboard", true)
    fun setClipboard(c: Context, on: Boolean) = prefs(c).edit().putBoolean("clipboard", on).apply()
    /** A PC may tap, swipe and type on this phone while its screen is shared with it. Off by default. */
    fun screenControl(c: Context) = prefs(c).getBoolean("screenControl", false)
    fun setScreenControl(c: Context, on: Boolean) = prefs(c).edit().putBoolean("screenControl", on).apply()
    /** After one consent, screen sharing pauses instead of stopping, so the PC can reopen it without asking again. */
    fun screenReady(c: Context) = prefs(c).getBoolean("screenReady", false)
    fun setScreenReady(c: Context, on: Boolean) = prefs(c).edit().putBoolean("screenReady", on).apply()
}
