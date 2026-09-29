package dev.palwyn.drop

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.OpenableColumns
import android.text.format.Formatter
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.IntentCompat
import dev.palwyn.link.Link
import dev.palwyn.link.LinkServer
import dev.palwyn.ui.PalwynTheme
import kotlinx.coroutines.delay

/**
 * "Share" > Palwyn: sends files, text or a link to the connected PC. Stays open until the PC has everything,
 * because read access to shared files lasts only as long as this screen.
 */
class ShareActivity : ComponentActivity() {
    private var offer: Drops.Offer? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val (items, skipped) = items(intent)
        val text = intent.getStringExtra(Intent.EXTRA_TEXT)?.takeIf { items.isEmpty() && it.isNotBlank() }
        val connected = LinkServer.hasSessions()
        if (connected && (items.isNotEmpty() || text != null)) offer = Drops.offer(items, text)
        setContent {
            PalwynTheme {
                Sheet(onClose = ::finish) {
                    SendCard(offer, Link.pcName.value ?: "your PC", connected, skipped, onClose = ::finish)
                }
            }
        }
    }

    override fun onDestroy() {
        // Leaving early ends the offer: the PC can no longer pull what it hasn't got.
        offer?.let { if (it.state.value.ok == null) Drops.cancel(it) }
        super.onDestroy()
    }

    /** Shared files with a known size, at most 50, plus how many were left out. */
    private fun items(intent: Intent): Pair<List<Drops.Item>, Int> {
        val uris = when (intent.action) {
            Intent.ACTION_SEND -> listOfNotNull(IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java))
            Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
            else -> emptyList()
        }
        // Never our own files (clipboard, camera, MMS cache): another app can't use a share to have us send them.
        val items = uris.filter { it.authority != "$packageName.files" }.take(50).mapNotNull { uri ->
            try {
                contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE), null, null, null)?.use {
                    if (!it.moveToFirst() || it.isNull(1)) null
                    else Drops.Item(uri, Drops.safeName(it.getString(0) ?: "file"), it.getLong(1),
                        contentResolver.getType(uri) ?: "application/octet-stream")
                }
            } catch (e: SecurityException) {
                null
            }
        }
        return items to (uris.size - items.size)
    }
}

@Composable
private fun Sheet(onClose: () -> Unit, content: @Composable () -> Unit) {
    Box(
        Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.32f))
            .clickable(remember { MutableInteractionSource() }, indication = null, onClick = onClose),
        contentAlignment = Alignment.BottomCenter,
    ) {
        Surface(
            shape = MaterialTheme.shapes.extraLarge,
            color = MaterialTheme.colorScheme.surfaceContainerHigh,
            modifier = Modifier.fillMaxWidth().padding(12.dp).navigationBarsPadding()
                .clickable(remember { MutableInteractionSource() }, indication = null) { },
        ) { content() }
    }
}

@Composable
private fun SendCard(offer: Drops.Offer?, pcName: String, connected: Boolean, skipped: Int, onClose: () -> Unit) {
    val context = androidx.compose.ui.platform.LocalContext.current
    val running = offer?.state?.collectAsState()?.value?.ok == null && offer != null && connected
    Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Text("Send to $pcName", style = MaterialTheme.typography.titleLarge)
        when {
            !connected -> Text(
                "Your PC isn't connected. Open Palwyn on your PC, on the same Wi-Fi, and share again.",
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            offer == null -> Text("There's nothing here Palwyn can send.", color = MaterialTheme.colorScheme.onSurfaceVariant)
            else -> {
                val state by offer.state.collectAsState()
                val what = when {
                    offer.items.isEmpty() -> offer.text.orEmpty()
                    offer.items.size == 1 -> offer.items[0].name
                    else -> "${offer.items.size} files"
                }
                Text(what, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (offer.items.isNotEmpty()) {
                    Text(
                        "${Formatter.formatShortFileSize(context, state.sent)} of ${Formatter.formatShortFileSize(context, state.total)}",
                        style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
                when (state.ok) {
                    null -> LinearProgressIndicator(
                        progress = { if (state.total > 0) state.sent.toFloat() / state.total else 0f },
                        modifier = Modifier.fillMaxWidth(),
                    )
                    true -> {
                        Text("Sent. It's in Downloads > Palwyn on your PC.", color = MaterialTheme.colorScheme.primary)
                        LaunchedEffect(Unit) {
                            delay(1500)
                            onClose()
                        }
                    }
                    false -> Text("Your PC couldn't receive it. Try again.", color = MaterialTheme.colorScheme.error)
                }
                if (skipped > 0) Text(
                    "$skipped item${if (skipped == 1) "" else "s"} couldn't be sent (unknown size, or over the 50-file limit).",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
        TextButton(onClick = onClose, modifier = Modifier.align(Alignment.End)) {
            Text(if (running) "Cancel" else "Close")
        }
    }
}
