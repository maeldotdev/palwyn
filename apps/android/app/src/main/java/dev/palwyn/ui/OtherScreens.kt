package dev.palwyn.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.outlined.Computer
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.RestartAlt
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.compose.material3.TextButton
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.palwyn.Prefs
import dev.palwyn.identity.DeviceIdentity
import dev.palwyn.identity.Fingerprint
import dev.palwyn.link.Link
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkState
import dev.palwyn.link.PairedPcs
import dev.palwyn.screen.ControlService
import dev.palwyn.screen.Screen
import dev.palwyn.screen.ScreenService
import androidx.compose.material.icons.outlined.Cast
import android.content.Intent
import android.provider.Settings
import androidx.compose.material.icons.outlined.ScreenShare
import androidx.lifecycle.compose.LifecycleResumeEffect
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.DateFormat
import java.util.Date

@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun SubScreen(title: String, onBack: () -> Unit, content: @Composable () -> Unit) {
    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(title) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back")
                    }
                },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).verticalScroll(rememberScrollState())) { content() }
    }
}

@Composable
fun SettingsScreen(onBack: () -> Unit) = SubScreen("Settings", onBack) {
    val context = LocalContext.current
    var autoStart by remember { mutableStateOf(Prefs.autoStart(context)) }
    val linkState by Link.state.collectAsStateWithLifecycle()
    val connectedName by Link.pcName.collectAsStateWithLifecycle()
    // Re-read when the link state changes (pairing, unpairing, connecting).
    val pcs = remember(linkState, connectedName) { PairedPcs.all(context) }
    var removed by remember { mutableStateOf(setOf<String>()) }

    if (pcs.isNotEmpty()) {
        SectionTitle("Paired PCs", start = 16.dp)
        pcs.filterNot { it.fingerprint in removed }.forEach { pc ->
            val live = linkState == LinkState.Connected && pc.name == connectedName
            ListItem(
                leadingContent = { Icon(Icons.Outlined.Computer, contentDescription = null) },
                headlineContent = { Text(pc.name) },
                supportingContent = {
                    Text((if (live) "Connected now. " else "") +
                        "Paired " + DateFormat.getDateInstance(DateFormat.MEDIUM).format(Date(pc.pairedAt)))
                },
                trailingContent = {
                    TextButton(onClick = {
                        removed = removed + pc.fingerprint
                        LinkServer.unpair(pc.fingerprint)
                    }) { Text("Remove") }
                },
            )
        }
        ListItem(
            leadingContent = { Icon(Icons.Outlined.Lock, contentDescription = null) },
            headlineContent = { Text("Encrypted end to end") },
            supportingContent = {
                Text("TLS 1.3 with the keys exchanged when you paired. Only these PCs can connect, and nothing passes through a server.")
            },
        )
    }
    val version = remember { context.packageManager.getPackageInfo(context.packageName, 0).versionName }
    val deviceId by produceState<String?>(null) {
        value = withContext(Dispatchers.IO) { Fingerprint.display(DeviceIdentity.deviceId()) }
    }

    SectionTitle("General", start = 16.dp)
    ListItem(
        leadingContent = { Icon(Icons.Outlined.RestartAlt, contentDescription = null) },
        headlineContent = { Text("Start automatically") },
        supportingContent = { Text("After your phone restarts") },
        trailingContent = {
            Switch(checked = autoStart, onCheckedChange = {
                autoStart = it
                Prefs.setAutoStart(context, it)
            })
        },
    )
    var clipboard by remember { mutableStateOf(Prefs.clipboard(context)) }
    ListItem(
        leadingContent = { Icon(Icons.Outlined.ContentPaste, contentDescription = null) },
        headlineContent = { Text("Share clipboard with your PC") },
        supportingContent = {
            Text("Receive what your PC copies, and send yours when you tap Send clipboard to PC or Send to PC on selected text")
        },
        trailingContent = {
            Switch(checked = clipboard, onCheckedChange = {
                clipboard = it
                Prefs.setClipboard(context, it)
                LinkServer.permissionsChanged() // tells the PC, which then stops sending
            })
        },
    )

    var control by remember { mutableStateOf(Prefs.screenControl(context)) }
    // Re-read on return from Android's accessibility settings.
    var serviceOn by remember { mutableStateOf(ControlService.instance != null) }
    LifecycleResumeEffect(Unit) {
        serviceOn = ControlService.instance != null
        onPauseOrDispose { }
    }
    ListItem(
        leadingContent = { Icon(Icons.Outlined.ScreenShare, contentDescription = null) },
        headlineContent = { Text("Let my PC control this phone") },
        supportingContent = {
            Text(
                when {
                    !control -> "While you show this screen on your PC, it can tap, swipe and type here. Off: the PC can only watch."
                    serviceOn -> "On while you show this screen on your PC. Stop sharing and it stops too."
                    else -> "One more step: in Android's Accessibility settings, turn on Palwyn PC control. " +
                        "If Android says it's a restricted setting, open App info > ⋮ > Allow restricted settings first."
                }
            )
        },
        trailingContent = {
            Switch(checked = control, onCheckedChange = {
                control = it
                Prefs.setScreenControl(context, it)
                LinkServer.permissionsChanged()
                if (it && ControlService.instance == null) context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS))
            })
        },
    )
    if (control && !serviceOn) TextButton(
        onClick = { context.startActivity(Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS)) },
        modifier = Modifier.padding(start = 56.dp),
    ) { Text("Open Accessibility settings") }

    var ready by remember { mutableStateOf(Prefs.screenReady(context)) }
    ListItem(
        leadingContent = { Icon(Icons.Outlined.Cast, contentDescription = null) },
        headlineContent = { Text("Keep screen sharing ready") },
        supportingContent = {
            Text(
                "Android asks once. After that your PC can show this screen again without you touching the phone, " +
                    "until you tap Stop in the notification or restart the phone. Off: Android asks every time."
            )
        },
        trailingContent = {
            Switch(checked = ready, onCheckedChange = {
                ready = it
                Prefs.setScreenReady(context, it)
                if (!it && Screen.paused) ScreenService.stop(context)
            })
        },
    )

    SectionTitle("Privacy", start = 16.dp)
    listOf(
        "Where your data goes" to "Only to the PCs listed above, straight over your Wi-Fi. No account, no cloud.",
        "What your PC keeps" to "Calls, messages and notifications are shown on the PC but not stored there. Photos and files are copied only when you open, save or send them.",
        "Clipboard" to "Never leaves this phone on its own, only when you tap send. Passwords marked sensitive are never sent.",
        "Permissions" to "Each feature works only after you allow it, and you can take any permission back in Android settings.",
    ).forEach { (title, body) ->
        ListItem(headlineContent = { Text(title) }, supportingContent = { Text(body) })
    }

    SectionTitle("About", start = 16.dp)
    ListItem(headlineContent = { Text("Version") }, trailingContent = { Text(version ?: "") })
    ListItem(
        headlineContent = { Text("Device ID") },
        supportingContent = { Text("Your PC shows the same ID, so you can check it's this phone") },
        trailingContent = { Text(deviceId ?: "…") },
    )
}
