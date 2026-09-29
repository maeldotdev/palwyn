package dev.palwyn.setup

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.os.PowerManager
import dev.palwyn.notifications.Mirror
import dev.palwyn.photos.Photos

enum class SetupStep { Notifications, Background, Calls, Messages, Mirror, Photos, Contacts }

/** [permissions]: runtime permissions this step requests; empty for steps done in system settings. */
data class SetupItem(
    val step: SetupStep,
    val title: String,
    val reason: String,
    val done: Boolean,
    val permissions: List<String> = emptyList(),
)

/**
 * Permissions and system settings Palwyn needs, with the reason shown to the user. Each feature phase
 * adds its own steps when that feature exists.
 */
object Setup {
    val CALL_PERMISSIONS = listOf(
        Manifest.permission.READ_PHONE_STATE,
        Manifest.permission.READ_CALL_LOG,
        Manifest.permission.ANSWER_PHONE_CALLS,
    )
    val CONTACT_PERMISSIONS = listOf(Manifest.permission.READ_CONTACTS, Manifest.permission.WRITE_CONTACTS)

    val MESSAGE_PERMISSIONS = listOf(Manifest.permission.READ_SMS, Manifest.permission.SEND_SMS)

    fun items(c: Context): List<SetupItem> = buildList {
        fun granted(p: String) = c.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED
        if (Build.VERSION.SDK_INT >= 33) {
            add(
                SetupItem(
                    SetupStep.Notifications,
                    "Show connection status",
                    "A small notification tells you Palwyn is running and ready for your PC.",
                    granted(Manifest.permission.POST_NOTIFICATIONS),
                    listOf(Manifest.permission.POST_NOTIFICATIONS),
                )
            )
        }
        add(
            SetupItem(
                SetupStep.Background,
                "Run in the background",
                "Lets your PC reach this phone while the screen is off.",
                c.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(c.packageName),
            )
        )
        add(
            SetupItem(
                SetupStep.Calls,
                "Calls on your PC",
                "See who's calling, answer, decline or hang up from your PC, and see recent calls there. " +
                    "You still talk on this phone.",
                CALL_PERMISSIONS.all(::granted),
                CALL_PERMISSIONS,
            )
        )
        add(
            SetupItem(
                SetupStep.Messages,
                "Messages on your PC",
                "Read your text messages on your PC, get alerts for new ones, and reply from there. " +
                    "Replies are sent by this phone, at your plan's usual SMS rate.",
                MESSAGE_PERMISSIONS.all(::granted),
                MESSAGE_PERMISSIONS,
            )
        )
        add(
            SetupItem(
                SetupStep.Mirror,
                "Phone notifications on your PC",
                "See your apps' notifications on your PC, and dismiss or reply to them there. " +
                    "If the switch is greyed out: App info, then the menu, then Allow restricted settings.",
                Mirror.granted(c),
            )
        )
        add(
            SetupItem(
                SetupStep.Photos,
                "Photos and videos on your PC",
                "Browse this phone's photos and videos on your PC and copy the ones you want. Full-size files are only sent when you open or save them.",
                Photos.complete(c),
                Photos.PERMISSIONS,
            )
        )
        add(
            SetupItem(
                SetupStep.Contacts,
                "Contacts on your PC",
                "See who's calling or texting by name, and look up, add, edit or delete contacts from your PC. " +
                    "Changes are saved on this phone. The PC doesn't keep a copy.",
                CONTACT_PERMISSIONS.all(::granted),
                CONTACT_PERMISSIONS,
            )
        )
    }
}
