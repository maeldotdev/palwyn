package dev.palwyn.ui

import android.content.Intent
import android.os.Bundle
import android.view.KeyEvent
import dev.palwyn.remote.Remote
import androidx.compose.runtime.MutableState
import dev.palwyn.pairing.PairingInvite
import dev.palwyn.pairing.PairingManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.material3.Typography
import androidx.compose.ui.text.ExperimentalTextApi
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontVariation
import androidx.compose.ui.text.font.FontWeight
import dev.palwyn.R
import dev.palwyn.link.LinkService

class MainActivity : ComponentActivity() {
    private val openPairing = mutableStateOf(false)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        LinkService.start(this)
        handle(intent)
        setContent { PalwynTheme { App(openPairing) } }
    }

    // On the Remote's Slides tab the volume keys change slides instead of the phone's volume.
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val keys = Remote.volumeKeys
        val forward = when (event.keyCode) {
            KeyEvent.KEYCODE_VOLUME_UP -> true
            KeyEvent.KEYCODE_VOLUME_DOWN -> false
            else -> null
        }
        if (keys == null || forward == null) return super.dispatchKeyEvent(event)
        if (event.action == KeyEvent.ACTION_DOWN && event.repeatCount == 0) keys(forward)
        return true
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun handle(intent: Intent?) {
        val invite = intent?.data?.toString()?.let(PairingInvite::parse) ?: return
        PairingManager.offer(invite)
        openPairing.value = true
    }
}

private enum class Screen { Home, Pair, Settings, Remote }

@Composable
private fun App(openPairing: MutableState<Boolean>) {
    var screen by rememberSaveable { mutableStateOf(Screen.Home) }
    var remoteTab by rememberSaveable { mutableStateOf(0) }
    if (openPairing.value) {
        screen = Screen.Pair
        openPairing.value = false
    }
    BackHandler(enabled = screen != Screen.Home) { screen = Screen.Home }
    AnimatedContent(targetState = screen, transitionSpec = { fadeIn() togetherWith fadeOut() }, label = "screen") {
        when (it) {
            Screen.Home -> HomeScreen(onPair = { screen = Screen.Pair }, onSettings = { screen = Screen.Settings }, onRemote = { tab -> remoteTab = tab; screen = Screen.Remote })
            Screen.Pair -> PairScreen(onBack = { screen = Screen.Home })
            Screen.Settings -> SettingsScreen(onBack = { screen = Screen.Home })
            Screen.Remote -> RemoteScreen(onBack = { screen = Screen.Home }, startTab = remoteTab)
        }
    }
}

/** The brand's green on neutral greys, in both themes. Same colours as the Windows app. */
private val LightColors = lightColorScheme(
    primary = Color(0xFF0B7A5B), onPrimary = Color.White,
    primaryContainer = Color(0xFFD2F4E7), onPrimaryContainer = Color(0xFF04342A),
    secondary = Color(0xFF4B5563), onSecondary = Color.White,
    secondaryContainer = Color(0xFFE9ECEE), onSecondaryContainer = Color(0xFF111418),
    background = Color(0xFFFBFBFC), onBackground = Color(0xFF111418),
    surface = Color(0xFFFBFBFC), onSurface = Color(0xFF111418), onSurfaceVariant = Color(0xFF5B6470),
    surfaceContainerLowest = Color.White, surfaceContainerLow = Color(0xFFF6F7F8),
    surfaceContainer = Color(0xFFF1F3F4), surfaceContainerHigh = Color(0xFFE9ECEE), surfaceContainerHighest = Color(0xFFE2E6E8),
    outline = Color(0xFF8A929B), outlineVariant = Color(0xFFE2E6EA),
)
private val DarkColors = darkColorScheme(
    primary = Color(0xFF2ED3A1), onPrimary = Color(0xFF05281D),
    primaryContainer = Color(0xFF0F4D3B), onPrimaryContainer = Color(0xFFB5F2DC),
    secondary = Color(0xFFA7B0BA), onSecondary = Color(0xFF111418),
    secondaryContainer = Color(0xFF1A1F22), onSecondaryContainer = Color(0xFFE6E8EA),
    background = Color(0xFF0B0D0E), onBackground = Color(0xFFE6E8EA),
    surface = Color(0xFF0B0D0E), onSurface = Color(0xFFE6E8EA), onSurfaceVariant = Color(0xFF8B949E),
    surfaceContainerLowest = Color(0xFF070809), surfaceContainerLow = Color(0xFF0F1213),
    surfaceContainer = Color(0xFF131719), surfaceContainerHigh = Color(0xFF1A1F22), surfaceContainerHighest = Color(0xFF22282C),
    outline = Color(0xFF5C6670), outlineVariant = Color(0xFF22282C),
)

/** JetBrains Mono (SIL OFL, res/raw/jetbrains_mono_ofl.txt): one variable font file, four weights. */
@OptIn(ExperimentalTextApi::class)
private val Mono = FontFamily(
    listOf(400, 500, 600, 700).map { w ->
        Font(R.font.jetbrains_mono, FontWeight(w), variationSettings = FontVariation.Settings(FontVariation.weight(w)))
    }
)

private val MonoTypography = Typography().run {
    copy(
        displayLarge = displayLarge.copy(fontFamily = Mono), displayMedium = displayMedium.copy(fontFamily = Mono),
        displaySmall = displaySmall.copy(fontFamily = Mono), headlineLarge = headlineLarge.copy(fontFamily = Mono),
        headlineMedium = headlineMedium.copy(fontFamily = Mono), headlineSmall = headlineSmall.copy(fontFamily = Mono),
        titleLarge = titleLarge.copy(fontFamily = Mono, fontWeight = FontWeight.Bold), titleMedium = titleMedium.copy(fontFamily = Mono),
        titleSmall = titleSmall.copy(fontFamily = Mono), bodyLarge = bodyLarge.copy(fontFamily = Mono),
        bodyMedium = bodyMedium.copy(fontFamily = Mono), bodySmall = bodySmall.copy(fontFamily = Mono),
        labelLarge = labelLarge.copy(fontFamily = Mono), labelMedium = labelMedium.copy(fontFamily = Mono),
        labelSmall = labelSmall.copy(fontFamily = Mono),
    )
}

@Composable
fun PalwynTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = if (isSystemInDarkTheme()) DarkColors else LightColors, typography = MonoTypography, content = content)
}
