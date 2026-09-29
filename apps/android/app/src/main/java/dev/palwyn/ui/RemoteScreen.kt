package dev.palwyn.ui

import android.os.SystemClock
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material.icons.automirrored.outlined.VolumeOff
import androidx.compose.material.icons.automirrored.outlined.VolumeUp
import androidx.compose.material.icons.outlined.Lock
import androidx.compose.material.icons.outlined.Pause
import androidx.compose.material.icons.outlined.PlayArrow
import androidx.compose.material.icons.outlined.SkipNext
import androidx.compose.material.icons.outlined.SkipPrevious
import androidx.compose.material.icons.outlined.Terminal
import androidx.compose.material.icons.outlined.TouchApp
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilledIconButton
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.PrimaryTabRow
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.hapticfeedback.HapticFeedbackType
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalHapticFeedback
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.platform.LocalViewConfiguration
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.TextFieldValue
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.palwyn.remote.PcRemote
import dev.palwyn.remote.Remote
import kotlin.math.abs
import kotlin.math.roundToInt

private const val TAP_MS = 250L
private const val HOLD_MS = 450L

/** The phone as a remote for a connected PC: touchpad and keyboard, slide clicker, and the PC's media, lock and commands. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun RemoteScreen(onBack: () -> Unit, startTab: Int = 0) {
    val pcs by Remote.pcs.collectAsStateWithLifecycle()
    var picked by rememberSaveable { mutableStateOf<String?>(null) }
    val target = picked?.takeIf { it in pcs } ?: pcs.keys.firstOrNull()
    DisposableEffect(target) {
        Remote.target = target
        onDispose { Remote.target = null } // nothing is sent once this screen is gone
    }
    var tab by rememberSaveable { mutableIntStateOf(startTab) }
    val pc = target?.let(pcs::get)

    Scaffold(
        topBar = {
            TopAppBar(
                title = {
                    Column {
                        Text("Remote")
                        if (pc != null) Text(pc.pcName, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                },
                navigationIcon = { IconButton(onClick = onBack) { Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Back") } },
            )
        },
    ) { padding ->
        Column(Modifier.padding(padding).consumeWindowInsets(padding).imePadding().fillMaxSize()) {
            if (pc == null) {
                Notice("No PC to control", "Connect to your PC first. If it's connected, update Palwyn on the PC to use the remote.")
                return@Column
            }
            if (pcs.size > 1) Row(Modifier.padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                pcs.forEach { (fp, other) -> FilterChip(selected = fp == target, onClick = { picked = fp }, label = { Text(other.pcName) }) }
            }
            PrimaryTabRow(selectedTabIndex = tab) {
                listOf("Touchpad", "Slides", "PC").forEachIndexed { i, title ->
                    Tab(selected = tab == i, onClick = { tab = i }, text = { Text(title) })
                }
            }
            when (tab) {
                0 -> if (pc.input) TouchpadTab() else InputOff(pc)
                1 -> if (pc.input) SlidesTab() else InputOff(pc)
                else -> PcTab(pc)
            }
        }
    }
}

@Composable
private fun InputOff(pc: PcRemote) = Notice(
    "Turn on Mouse and keyboard on your PC",
    "In Palwyn on ${pc.pcName}, open Settings > Control from your phone. It's off until you allow it, since it's full control of the PC.",
)

@Composable
private fun Notice(title: String, body: String) {
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = MaterialTheme.shapes.large, modifier = Modifier.padding(16.dp).fillMaxWidth()) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium)
            Text(body, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

// ---- Touchpad and keyboard ----

@Composable
private fun TouchpadTab() {
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Touchpad(Modifier.weight(1f).fillMaxWidth())
        Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            listOf("Esc" to "escape", "Tab" to "tab", "←" to "left", "↑" to "up", "↓" to "down", "→" to "right", "⌫" to "backspace", "↵" to "enter")
                .forEach { (label, key) ->
                    TextButton(onClick = { Remote.key(key) }, contentPadding = PaddingValues(0.dp), modifier = Modifier.weight(1f)) { Text(label) }
                }
        }
        var field by remember { mutableStateOf(TextFieldValue("")) }
        OutlinedTextField(
            value = field,
            onValueChange = {
                Remote.typed(field.text, it.text)
                field = it
            },
            label = { Text("Type on your PC") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = {
                Remote.key("enter")
                field = TextFieldValue("")
            }),
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

/**
 * One finger moves the pointer, a tap clicks, a two-finger tap right-clicks, two fingers scroll, and holding still
 * before moving drags (the button stays down until the finger lifts).
 */
@Composable
private fun Touchpad(modifier: Modifier) {
    val density = LocalDensity.current.density
    val slop = LocalViewConfiguration.current.touchSlop
    val haptics = LocalHapticFeedback.current
    Surface(
        color = MaterialTheme.colorScheme.surfaceContainerHigh,
        shape = MaterialTheme.shapes.extraLarge,
        modifier = modifier.pointerInput(Unit) {
            awaitEachGesture {
                val start = awaitFirstDown(requireUnconsumed = false).uptimeMillis
                var fingers = 1
                var travel = 0f
                var dragging = false
                while (true) {
                    val waiting = !dragging && fingers == 1 && travel < slop
                    val left = HOLD_MS - (SystemClock.uptimeMillis() - start)
                    val event = if (waiting && left > 0) withTimeoutOrNull(left) { awaitPointerEvent() }
                    else if (waiting) null else awaitPointerEvent()
                    if (event == null) { // held still: press the button for a drag
                        dragging = true
                        haptics.performHapticFeedback(HapticFeedbackType.LongPress)
                        Remote.button("left", "down")
                        continue
                    }
                    val down = event.changes.filter { it.pressed }
                    if (down.isEmpty()) break
                    fingers = maxOf(fingers, down.size)
                    if (down.size >= 2) {
                        val dy = down.map { it.position.y - it.previousPosition.y }.average().toFloat() / density
                        travel += abs(dy) * density
                        if (travel > slop) Remote.scroll(dy * 4f) // 30 dp of finger travel is one wheel notch
                    } else if (fingers == 1) {
                        val d = down[0].position - down[0].previousPosition
                        travel += d.getDistance()
                        if (travel > slop || dragging) Remote.move(Remote.pixels(d.x / density), Remote.pixels(d.y / density))
                    }
                    event.changes.forEach { it.consume() }
                }
                val quick = SystemClock.uptimeMillis() - start < TAP_MS
                when {
                    dragging -> Remote.button("left", "up")
                    travel <= slop && quick -> Remote.button(if (fingers >= 2) "right" else "left", "click")
                }
            }
        },
    ) {
        Box(contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.padding(24.dp)) {
                Icon(Icons.Outlined.TouchApp, contentDescription = null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Text(
                    "Move with one finger. Tap to click, tap with two fingers to right-click, slide two fingers to scroll. Hold still, then move to drag.",
                    style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, textAlign = TextAlign.Center,
                )
            }
        }
    }
}

// ---- Slides ----

@Composable
private fun SlidesTab() {
    val view = LocalView.current
    DisposableEffect(Unit) {
        view.keepScreenOn = true // a presentation outlasts the screen timeout
        Remote.volumeKeys = { forward -> Remote.key(if (forward) "right" else "left") }
        onDispose {
            view.keepScreenOn = false
            Remote.volumeKeys = null
        }
    }
    Column(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Button(onClick = { Remote.key("right") }, shape = MaterialTheme.shapes.extraLarge, modifier = Modifier.weight(1f).fillMaxWidth()) {
            Icon(Icons.AutoMirrored.Filled.ArrowForward, contentDescription = null, modifier = Modifier.size(32.dp))
            Text("Next", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.padding(start = 12.dp))
        }
        FilledTonalButton(onClick = { Remote.key("left") }, shape = MaterialTheme.shapes.extraLarge, modifier = Modifier.height(88.dp).fillMaxWidth()) {
            Icon(Icons.AutoMirrored.Filled.ArrowBack, contentDescription = null)
            Text("Previous", style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(start = 12.dp))
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton(onClick = { Remote.key("f5") }, modifier = Modifier.weight(1f)) { Text("Start slideshow") }
            OutlinedButton(onClick = { Remote.key("escape") }, modifier = Modifier.weight(1f)) { Text("End") }
        }
        Text(
            "Volume up goes to the next slide, volume down to the previous one. The screen stays on here.",
            style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant,
        )
    }
}

// ---- The PC: media, volume, lock, commands ----

@Composable
private fun PcTab(pc: PcRemote) {
    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        if (pc.media) {
            NowPlaying(pc)
            pc.volume?.let { Volume(it, pc.muted == true) }
            OutlinedButton(onClick = Remote::lock, modifier = Modifier.fillMaxWidth()) {
                Icon(Icons.Outlined.Lock, contentDescription = null, modifier = Modifier.size(18.dp))
                Text("Lock ${pc.pcName}", Modifier.padding(start = 8.dp), maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        } else Notice(
            "Turn on Music, volume and lock on your PC",
            "In Palwyn on ${pc.pcName}, open Settings > Control from your phone.",
        )
        SectionTitle("Commands", start = 4.dp)
        if (pc.commands.isEmpty()) Text(
            "None yet. Add them in Palwyn on your PC: Settings > Control from your phone > Commands.",
            style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, modifier = Modifier.padding(horizontal = 4.dp),
        )
        pc.commands.forEach { (id, name) ->
            FilledTonalButton(onClick = { Remote.command(id, name) }, modifier = Modifier.fillMaxWidth()) {
                Icon(Icons.Outlined.Terminal, contentDescription = null, modifier = Modifier.size(18.dp))
                Text(name, Modifier.padding(start = 8.dp).weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
    }
}

@Composable
private fun NowPlaying(pc: PcRemote) {
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = MaterialTheme.shapes.large, modifier = Modifier.fillMaxWidth()) {
        Column(Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
            Text(pc.title ?: "Nothing playing", style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
            val detail = listOfNotNull(pc.artist, pc.app).joinToString(" · ")
            if (detail.isNotEmpty()) Text(detail, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                MediaButton(Icons.Outlined.SkipPrevious, "Previous") { Remote.media("previous") }
                FilledIconButton(onClick = { Remote.media("playPause") }, modifier = Modifier.padding(horizontal = 16.dp).size(56.dp)) {
                    Icon(if (pc.playing == true) Icons.Outlined.Pause else Icons.Outlined.PlayArrow, contentDescription = if (pc.playing == true) "Pause" else "Play")
                }
                MediaButton(Icons.Outlined.SkipNext, "Next") { Remote.media("next") }
            }
        }
    }
}

@Composable
private fun MediaButton(icon: ImageVector, label: String, onClick: () -> Unit) =
    IconButton(onClick = onClick, modifier = Modifier.size(48.dp)) { Icon(icon, contentDescription = label) }

@Composable
private fun Volume(level: Int, muted: Boolean) {
    var value by remember { mutableFloatStateOf(level.toFloat()) }
    var dragging by remember { mutableStateOf(false) }
    LaunchedEffect(level) { if (!dragging) value = level.toFloat() } // the PC's own changes, unless the user is sliding
    Surface(color = MaterialTheme.colorScheme.surfaceContainer, shape = MaterialTheme.shapes.large, modifier = Modifier.fillMaxWidth()) {
        Row(Modifier.padding(horizontal = 8.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            IconButton(onClick = { Remote.mute(!muted) }) {
                Icon(
                    if (muted) Icons.AutoMirrored.Outlined.VolumeOff else Icons.AutoMirrored.Outlined.VolumeUp,
                    contentDescription = if (muted) "Unmute your PC" else "Mute your PC",
                )
            }
            Slider(
                value = value,
                onValueChange = {
                    dragging = true
                    value = it
                    Remote.volume(it.roundToInt())
                },
                onValueChangeFinished = { dragging = false },
                valueRange = 0f..100f,
                modifier = Modifier.weight(1f),
            )
            Text("${value.roundToInt()}", style = MaterialTheme.typography.labelLarge, textAlign = TextAlign.End, modifier = Modifier.width(40.dp).padding(end = 8.dp))
        }
    }
}
