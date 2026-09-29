package dev.palwyn.ui

import android.content.ComponentName
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.text.format.Formatter
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.outlined.Link
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Slideshow
import androidx.compose.material3.LocalContentColor
import androidx.compose.ui.text.font.FontWeight
import dev.palwyn.remote.Remote
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.outlined.BatteryChargingFull
import androidx.compose.material.icons.outlined.BatteryStd
import androidx.compose.material.icons.outlined.Call
import androidx.compose.material.icons.outlined.CheckCircle
import androidx.compose.material.icons.outlined.ContentPaste
import androidx.compose.material.icons.outlined.Contacts
import androidx.compose.material.icons.outlined.NotificationsActive
import androidx.compose.material.icons.outlined.PhoneAndroid
import androidx.compose.material.icons.outlined.PhotoLibrary
import androidx.compose.material.icons.outlined.Sd
import androidx.compose.material.icons.outlined.Settings
import androidx.compose.material.icons.outlined.Sms
import androidx.compose.material.icons.outlined.Upload
import androidx.compose.material.icons.outlined.Wifi
import androidx.compose.material.icons.outlined.WifiOff
import androidx.compose.material.icons.outlined.Bolt
import androidx.compose.material.icons.outlined.Notifications
import androidx.compose.material.icons.outlined.Mouse
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.ListItem
import androidx.compose.material3.ListItemDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.LifecycleResumeEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.palwyn.clipboard.ClipboardSync
import dev.palwyn.device.PhoneMonitor
import dev.palwyn.device.PhoneSnapshot
import dev.palwyn.drop.ShareActivity
import dev.palwyn.link.Link
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkState
import dev.palwyn.notifications.MirrorListener
import dev.palwyn.setup.Setup
import dev.palwyn.setup.SetupItem
import dev.palwyn.setup.SetupStep

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(onPair: () -> Unit, onSettings: () -> Unit, onRemote: (tab: Int) -> Unit) {
    val context = LocalContext.current
    val phone by PhoneMonitor.snapshot.collectAsStateWithLifecycle()
    val link by Link.state.collectAsStateWithLifecycle()
    val pcName by Link.pcName.collectAsStateWithLifecycle()
    val remotePcs by Remote.pcs.collectAsStateWithLifecycle()
    var setup by remember { mutableStateOf(Setup.items(context)) }
    var asked by rememberSaveable { mutableStateOf(setOf<String>()) }

    fun refresh() {
        setup = Setup.items(context)
        LinkServer.permissionsChanged()
    }
    // Permissions can change in system settings while we're away.
    LifecycleResumeEffect(Unit) {
        refresh()
        onPauseOrDispose { }
    }
    val permissions = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) { refresh() }
    // Picked files go through the same screen as Share > Send to PC, so progress and errors look the same.
    val pickFiles = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        if (uris.isEmpty()) return@rememberLauncherForActivityResult
        context.startActivity(
            Intent(context, ShareActivity::class.java)
                .setAction(Intent.ACTION_SEND_MULTIPLE)
                .putParcelableArrayListExtra(Intent.EXTRA_STREAM, ArrayList<Uri>(uris))
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        )
    }

    fun act(item: SetupItem) {
        when {
            item.step == SetupStep.Background -> context.startActivity(
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS, Uri.parse("package:${context.packageName}"))
            )
            item.step == SetupStep.Mirror -> context.startActivity(
                if (Build.VERSION.SDK_INT >= 30) Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS).putExtra(
                    Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                    ComponentName(context, MirrorListener::class.java).flattenToString(),
                )
                else Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS)
            )
            item.step.name !in asked -> {
                asked = asked + item.step.name
                permissions.launch(item.permissions.toTypedArray())
            }
            // Android stops showing the prompt after repeated denials; the settings pages always work.
            item.step == SetupStep.Notifications -> context.startActivity(
                Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, context.packageName)
            )
            else -> context.startActivity(
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))
            )
        }
    }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Palwyn") },
                actions = {
                    if (link == LinkState.Connected) StatusPill()
                    IconButton(onClick = onSettings) { Icon(Icons.Outlined.Settings, contentDescription = "Settings") }
                },
            )
        },
    ) { padding ->
        // Pending steps first: they're what the user can do something about.
        val features = setup.sortedBy { it.done }
        LazyColumn(
            contentPadding = PaddingValues(
                start = 16.dp, end = 16.dp, top = padding.calculateTopPadding() + 8.dp,
                bottom = padding.calculateBottomPadding() + 24.dp,
            ),
            verticalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            item {
                if (link == LinkState.Connected) Bento(
                    pcName = pcName,
                    remotePc = remotePcs.keys.firstOrNull(),
                    // This screen has focus, so reading the clipboard here is allowed (Android 10+).
                    onSendClipboard = { Toast.makeText(context, ClipboardSync.sendCurrent(context), Toast.LENGTH_SHORT).show() },
                    onSendFiles = { pickFiles.launch(arrayOf("*/*")) },
                    onRemote = onRemote,
                )
                else StatusCard(link, pcName, onPair)
            }
            item {
                SectionTitle(if (features.any { !it.done }) "Finish setting up" else "Permissions")
            }
            item {
                Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = MaterialTheme.shapes.large) {
                    Column(Modifier.padding(vertical = 4.dp)) {
                        features.forEach { FeatureRow(it) { act(it) } }
                    }
                }
            }
            phone?.let {
                item { SectionTitle("This phone") }
                item { PhoneFacts(it) }
            }
        }
    }
}

@Composable
private fun StatusCard(link: LinkState, pcName: String?, onPair: () -> Unit) {
    val connected = link == LinkState.Connected
    Surface(
        color = if (connected) MaterialTheme.colorScheme.primaryContainer else MaterialTheme.colorScheme.surfaceContainerHigh,
        shape = MaterialTheme.shapes.extraLarge,
        modifier = Modifier.fillMaxWidth(),
    ) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                Box(
                    Modifier.size(48.dp).background(
                        if (connected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surfaceContainerHighest,
                        CircleShape,
                    ),
                    contentAlignment = Alignment.Center,
                ) {
                    Icon(
                        Icons.Outlined.PhoneAndroid, contentDescription = null,
                        tint = if (connected) MaterialTheme.colorScheme.onPrimary else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                Column(Modifier.weight(1f)) {
                    Text(
                        when (link) {
                            LinkState.Connected -> pcName ?: "Your PC"
                            LinkState.Connecting -> "Connecting…"
                            LinkState.Disconnected -> "Waiting for ${pcName ?: "your PC"}"
                            LinkState.NotPaired -> "Not paired yet"
                        },
                        style = MaterialTheme.typography.titleLarge,
                        maxLines = 1, overflow = TextOverflow.Ellipsis,
                    )
                    Text(
                        when (link) {
                            LinkState.Connected -> "Connected, encrypted, over Wi-Fi"
                            LinkState.Disconnected -> "Open Palwyn on your PC. Both must be on the same Wi-Fi."
                            LinkState.Connecting -> "Your PC is reaching this phone"
                            LinkState.NotPaired -> "Pair with Palwyn on your Windows PC"
                        },
                        style = MaterialTheme.typography.bodyMedium,
                        color = if (connected) MaterialTheme.colorScheme.onPrimaryContainer else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
            when (link) {
                LinkState.NotPaired -> Button(onClick = onPair) { Text("Pair with a PC") }
                else -> Unit
            }
        }
    }
}

/** "PC" with a link icon in the top bar while connected. */
@Composable
private fun StatusPill() {
    Surface(color = MaterialTheme.colorScheme.primaryContainer, contentColor = MaterialTheme.colorScheme.primary, shape = CircleShape) {
        Row(Modifier.padding(horizontal = 10.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            Icon(Icons.Outlined.Link, contentDescription = null, modifier = Modifier.size(14.dp))
            Text("PC", style = MaterialTheme.typography.labelMedium)
        }
    }
}

/**
 * Connected: the actions as tiles. Slides and Lock need a PC that shares its remote (Palwyn on the PC
 * sends PC_REMOTE), so that row only shows then.
 */
@Composable
private fun Bento(
    pcName: String?, remotePc: String?, onSendClipboard: () -> Unit, onSendFiles: () -> Unit, onRemote: (tab: Int) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Text(
            "${(pcName ?: "Your PC").uppercase()} · CONNECTED",
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1, overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 4.dp, bottom = 2.dp),
        )
        Tile(Icons.Outlined.Mouse, "Control your PC", Modifier.fillMaxWidth(), subtitle = "touchpad · slides · media",
            accent = true, height = 132.dp) { onRemote(0) }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Tile(Icons.Outlined.ContentPaste, "Clipboard", Modifier.weight(1f), onClick = onSendClipboard)
            Tile(Icons.Outlined.Upload, "Files", Modifier.weight(1f), onClick = onSendFiles)
        }
        if (remotePc != null) Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Tile(Icons.Outlined.Slideshow, "Slides", Modifier.weight(1f)) { onRemote(1) }
            Tile(Icons.Outlined.Lock, "Lock PC", Modifier.weight(1f)) { Remote.lock(remotePc) }
        }
    }
}

@Composable
private fun Tile(
    icon: ImageVector, title: String, modifier: Modifier, subtitle: String? = null, accent: Boolean = false,
    height: Dp = 96.dp, onClick: () -> Unit,
) {
    val colors = MaterialTheme.colorScheme
    Surface(
        onClick = onClick,
        color = if (accent) colors.primary else colors.surfaceContainer,
        contentColor = if (accent) colors.onPrimary else colors.onSurface,
        shape = RoundedCornerShape(18.dp),
        border = if (accent) null else BorderStroke(1.dp, colors.outlineVariant),
        modifier = modifier.height(height),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.SpaceBetween) {
            Icon(icon, contentDescription = null, tint = if (accent) colors.onPrimary else colors.primary, modifier = Modifier.size(24.dp))
            Column {
                Text(title, style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (subtitle != null) Text(subtitle, style = MaterialTheme.typography.bodySmall, color = LocalContentColor.current.copy(alpha = 0.8f))
            }
        }
    }
}

private fun SetupStep.icon(): ImageVector = when (this) {
    SetupStep.Notifications -> Icons.Outlined.Notifications
    SetupStep.Background -> Icons.Outlined.Bolt
    SetupStep.Calls -> Icons.Outlined.Call
    SetupStep.Messages -> Icons.Outlined.Sms
    SetupStep.Mirror -> Icons.Outlined.NotificationsActive
    SetupStep.Photos -> Icons.Outlined.PhotoLibrary
    SetupStep.Contacts -> Icons.Outlined.Contacts
}

/** A feature and whether it's allowed: allowed ones say so; the rest explain themselves and offer Allow. */
@Composable
private fun FeatureRow(item: SetupItem, onAllow: () -> Unit) {
    ListItem(
        leadingContent = {
            Icon(item.step.icon(), contentDescription = null,
                tint = if (item.done) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.primary)
        },
        headlineContent = { Text(item.title) },
        supportingContent = if (item.done) null else ({ Text(item.reason) }),
        trailingContent = {
            if (item.done) Icon(Icons.Outlined.CheckCircle, contentDescription = "Allowed", tint = MaterialTheme.colorScheme.primary)
            else TextButton(onClick = onAllow) { Text("Allow") }
        },
        colors = ListItemDefaults.colors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
    )
}

@Composable
private fun PhoneFacts(p: PhoneSnapshot) {
    val context = LocalContext.current
    val facts = listOf(
        Triple(if (p.charging) Icons.Outlined.BatteryChargingFull else Icons.Outlined.BatteryStd, "Battery",
            p.batteryPercent?.let { "$it%" + if (p.charging) ", charging" else "" } ?: "Unknown"),
        Triple(Icons.Outlined.Sd, "Storage", Formatter.formatShortFileSize(context, p.storageFreeBytes) + " free"),
        Triple(if (p.onWifi) Icons.Outlined.Wifi else Icons.Outlined.WifiOff, "Network", if (p.onWifi) "Wi-Fi" else "Not on Wi-Fi"),
        Triple(Icons.Outlined.PhoneAndroid, "Phone", "${p.model}, Android ${p.androidVersion}"),
    )
    // Two columns: four short facts read faster side by side than as a tall list.
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        facts.chunked(2).forEach { row ->
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                row.forEach { (icon, label, value) ->
                    Surface(
                        color = MaterialTheme.colorScheme.surfaceContainer,
                        shape = MaterialTheme.shapes.large,
                        modifier = Modifier.weight(1f),
                    ) {
                        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Icon(icon, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.size(20.dp))
                            Text(label, style = MaterialTheme.typography.labelMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(value, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }
        }
    }
}

@Composable
fun SectionTitle(text: String, start: Dp = 8.dp) {
    Text(
        text,
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(start = start, top = 16.dp, bottom = 4.dp),
    )
}
