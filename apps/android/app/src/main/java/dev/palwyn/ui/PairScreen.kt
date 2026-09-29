package dev.palwyn.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilledTonalButton
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import dev.palwyn.R
import dev.palwyn.pairing.PairingManager
import dev.palwyn.pairing.PairingState

@Composable
fun PairScreen(onBack: () -> Unit) = SubScreen("Pair with a PC", onBack = {
    PairingManager.reset()
    onBack()
}) {
    val context = LocalContext.current
    val state by PairingManager.state.collectAsStateWithLifecycle()

    Column(Modifier.padding(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp)) {
        when (val s = state) {
            PairingState.Idle -> {
                Headline("Scan the code on your PC")
                Body(
                    "On your PC, open Palwyn and choose Add a phone. Point this phone's camera at the " +
                        "QR code it shows, then tap the Palwyn link."
                )
                FilledTonalButton(onClick = { PairingManager.startCode(context) }) { Text("Pair without a camera") }
            }

            is PairingState.Confirm -> {
                Headline("Pair with ${s.invite.pcName}?")
                Body("Only continue if you just scanned this code on your own PC. Both devices must be on the same Wi-Fi.")
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { PairingManager.acceptInvite(context) }) { Text("Pair") }
                    TextButton(onClick = { PairingManager.cancel(context) }) { Text("Cancel") }
                }
            }

            is PairingState.Waiting -> {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                    CircularProgressIndicator(Modifier.size(24.dp), strokeWidth = 3.dp)
                    Headline(if (s.codeMode) "Ready to pair" else "Connecting to ${s.pcName}…")
                }
                Body(
                    if (s.codeMode) "On your PC, open Add a phone and choose this phone under No camera."
                    else "Keep Palwyn open on your PC. This usually takes a few seconds."
                )
                TextButton(onClick = { PairingManager.cancel(context) }) { Text("Cancel") }
            }

            is PairingState.ShowCode -> {
                Headline("Check the code")
                val spaced = "${s.code.take(3)} ${s.code.drop(3)}"
                Text(
                    spaced,
                    style = MaterialTheme.typography.displayMedium,
                    modifier = Modifier.semantics { contentDescription = s.code.toCharArray().joinToString(" ") },
                )
                Body("Does your PC show the same code?")
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = { PairingManager.answerCode(true) }) { Text("Codes match") }
                    TextButton(onClick = { PairingManager.answerCode(false) }) { Text("Cancel") }
                }
            }

            is PairingState.Done -> {
                Icon(
                    painterResource(R.drawable.ic_notification),
                    contentDescription = null,
                    tint = MaterialTheme.colorScheme.primary,
                    modifier = Modifier.size(40.dp),
                )
                Headline("Paired with ${s.pcName}")
                Body("Your PC can now reach this phone whenever both are on the same Wi-Fi.")
                Button(onClick = {
                    PairingManager.reset()
                    onBack()
                }) { Text("Done") }
            }

            is PairingState.Failed -> {
                Headline("Pairing didn't finish")
                Body(s.message)
                Button(onClick = { PairingManager.reset() }) { Text("Try again") }
            }
        }
    }
}

@Composable
private fun Headline(text: String) = Text(text, style = MaterialTheme.typography.headlineSmall)

@Composable
private fun Body(text: String) =
    Text(text, style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.onSurfaceVariant)
