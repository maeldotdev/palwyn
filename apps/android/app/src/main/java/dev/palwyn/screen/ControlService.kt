package dev.palwyn.screen

import android.accessibilityservice.AccessibilityService
import android.accessibilityservice.GestureDescription
import android.content.Intent
import android.graphics.Path
import android.os.Build
import android.os.Bundle
import android.util.DisplayMetrics
import android.view.WindowManager
import android.view.accessibility.AccessibilityEvent
import android.view.accessibility.AccessibilityNodeInfo
import dev.palwyn.link.LinkServer

/**
 * Lets the PC the screen is shared with tap, swipe and type on this phone. Android offers this only to an
 * accessibility service the user turns on in Settings; Palwyn also needs its own switch, and LinkServer
 * advertises "screen.control" only while the screen is being shared.
 */
class ControlService : AccessibilityService() {
    override fun onServiceConnected() {
        instance = this
        LinkServer.permissionsChanged()
    }

    override fun onUnbind(intent: Intent?): Boolean {
        instance = null
        LinkServer.permissionsChanged()
        return super.onUnbind(intent)
    }

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}
    override fun onInterrupt() {}

    /** A tap (same start and end) or a straight swipe. Coordinates are 0–10000 across the screen as it's shown now. */
    fun touch(x1: Int, y1: Int, x2: Int, y2: Int, ms: Int): Boolean {
        val m = DisplayMetrics()
        @Suppress("DEPRECATION") getSystemService(WindowManager::class.java).defaultDisplay.getRealMetrics(m)
        fun x(v: Int) = v / 10_000f * (m.widthPixels - 1)
        fun y(v: Int) = v / 10_000f * (m.heightPixels - 1)
        val path = Path().apply {
            moveTo(x(x1), y(y1))
            if (x1 != x2 || y1 != y2) lineTo(x(x2), y(y2))
        }
        val stroke = GestureDescription.StrokeDescription(path, 0, ms.toLong().coerceIn(1, 10_000))
        return dispatchGesture(GestureDescription.Builder().addStroke(stroke).build(), null, null)
    }

    fun key(key: String): Boolean = when (key) {
        "back" -> performGlobalAction(GLOBAL_ACTION_BACK)
        "home" -> performGlobalAction(GLOBAL_ACTION_HOME)
        "recents" -> performGlobalAction(GLOBAL_ACTION_RECENTS)
        "enter" -> focused()?.let {
            Build.VERSION.SDK_INT >= 30 && it.performAction(AccessibilityNodeInfo.AccessibilityAction.ACTION_IME_ENTER.id)
        } == true
        "backspace" -> edit { it.dropLast(1) }
        else -> false
    }

    /** Typing goes into the focused text field; accessibility can't press real keys, so there are no shortcuts. */
    fun type(text: String) = edit { it + text }

    private fun focused(): AccessibilityNodeInfo? = rootInActiveWindow?.findFocus(AccessibilityNodeInfo.FOCUS_INPUT)

    private fun edit(change: (String) -> String): Boolean {
        val field = focused() ?: return false
        val current = if (Build.VERSION.SDK_INT >= 26 && field.isShowingHintText) "" else field.text?.toString() ?: ""
        val args = Bundle().apply { putCharSequence(AccessibilityNodeInfo.ACTION_ARGUMENT_SET_TEXT_CHARSEQUENCE, change(current)) }
        return field.performAction(AccessibilityNodeInfo.ACTION_SET_TEXT, args)
    }

    companion object {
        /** Set while the user has the service on in Android's accessibility settings. */
        @Volatile var instance: ControlService? = null
            private set
    }
}
