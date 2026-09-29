package dev.palwyn.drop

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.os.Bundle
import android.provider.MediaStore
import android.util.Log
import android.widget.Toast
import androidx.core.content.FileProvider
import dev.palwyn.R
import dev.palwyn.link.LinkServer
import dev.palwyn.link.LinkService
import java.io.File

/**
 * "Take a photo" from the PC (CAMERA_REQUEST). Android doesn't let a background app open the camera, so the
 * phone shows a notification; tapping it opens the phone's own camera app, and the photo goes to the PC as a
 * Quick Drop marked "camera". Palwyn never uses the camera itself, so it needs no camera permission.
 */
object Camera {
    private const val CHANNEL = "camera"
    private const val NOTIFICATION = 40

    fun request(c: Context) {
        val nm = c.getSystemService(NotificationManager::class.java)
        nm.createNotificationChannel(NotificationChannel(CHANNEL, "Photo requests from your PC", NotificationManager.IMPORTANCE_HIGH))
        val open = PendingIntent.getActivity(c, NOTIFICATION, Intent(c, CameraActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        nm.notify(NOTIFICATION, Notification.Builder(c, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle("Take a photo for your PC")
            .setContentText("Tap to open the camera. The photo goes straight to your PC.")
            .setContentIntent(open)
            .setAutoCancel(true)
            .setTimeoutAfter(120_000)
            .build())
        Log.i(LinkService.TAG, "Photo requested by the PC")
    }

    fun dismiss(c: Context) = c.getSystemService(NotificationManager::class.java).cancel(NOTIFICATION)
}

/** Opens the camera app with a file of ours to write to, then offers the photo to the PC. */
class CameraActivity : Activity() {
    private lateinit var file: File

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        // Recreated after Android reclaimed us while the camera was open: the result is still coming.
        savedInstanceState?.getString("file")?.let {
            file = File(it)
            return
        }
        Camera.dismiss(this)
        val dir = File(cacheDir, "camera").apply { mkdirs() }
        dir.listFiles()?.forEach { it.delete() } // the last photo was pulled by now, or never will be
        file = File(dir, "Photo ${java.text.SimpleDateFormat("yyyy-MM-dd HH.mm.ss", java.util.Locale.US).format(java.util.Date())}.jpg")
        val uri = FileProvider.getUriForFile(this, "$packageName.files", file)
        val capture = Intent(MediaStore.ACTION_IMAGE_CAPTURE).putExtra(MediaStore.EXTRA_OUTPUT, uri)
            .addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try {
            @Suppress("DEPRECATION") startActivityForResult(capture, 1)
        } catch (e: ActivityNotFoundException) {
            Toast.makeText(this, "No camera app found", Toast.LENGTH_SHORT).show()
            finish()
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        super.onSaveInstanceState(outState)
        outState.putString("file", file.path)
    }

    @Deprecated("Deprecated in Java")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        val message = when {
            resultCode != RESULT_OK || !file.exists() || file.length() == 0L -> {
                file.delete()
                null
            }
            !LinkServer.hasSessions() -> "Your PC isn't connected"
            else -> {
                Drops.offer(listOf(Drops.cacheItem(this, file, "image/jpeg")), null, "camera")
                "Sending the photo to your PC"
            }
        }
        message?.let { Toast.makeText(this, it, Toast.LENGTH_SHORT).show() }
        finish()
    }
}
