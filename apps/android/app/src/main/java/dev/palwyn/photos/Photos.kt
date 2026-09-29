package dev.palwyn.photos

import android.Manifest
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.graphics.Bitmap
import android.net.Uri
import android.os.Build
import android.provider.MediaStore
import android.provider.MediaStore.Files.FileColumns
import android.util.Base64
import android.util.Size
import org.json.JSONArray
import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.InputStream

/**
 * The phone's photos and videos through MediaStore (Phase 0: 256 px thumbnail in 86 ms on the reference phone).
 * On Android 14+ the user may grant only selected items; then only those are visible here, by design.
 */
object Photos {
    /** Runtime permissions to ask for on this Android version. */
    val PERMISSIONS: List<String> = when {
        Build.VERSION.SDK_INT >= 34 -> listOf(
            Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO,
            Manifest.permission.READ_MEDIA_VISUAL_USER_SELECTED,
        )
        Build.VERSION.SDK_INT >= 33 -> listOf(Manifest.permission.READ_MEDIA_IMAGES, Manifest.permission.READ_MEDIA_VIDEO)
        else -> listOf(Manifest.permission.READ_EXTERNAL_STORAGE)
    }

    /** Some access: the PC can list whatever is readable. */
    fun granted(c: Context) = PERMISSIONS.any { c.checkSelfPermission(it) == PackageManager.PERMISSION_GRANTED }

    /**
     * Full access, photos and videos. Android 13+ grants them separately (and 14+ can grant only selected
     * items), so with photos alone the PC sees no videos; setup keeps offering Allow until both are granted.
     */
    fun complete(c: Context): Boolean {
        fun has(p: String) = c.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED
        return if (Build.VERSION.SDK_INT >= 33) has(Manifest.permission.READ_MEDIA_IMAGES) && has(Manifest.permission.READ_MEDIA_VIDEO)
            else has(Manifest.permission.READ_EXTERNAL_STORAGE)
    }

    private fun uri(id: Long, video: Boolean): Uri = ContentUris.withAppendedId(
        if (video) MediaStore.Video.Media.EXTERNAL_CONTENT_URI else MediaStore.Images.Media.EXTERNAL_CONTENT_URI, id)

    /**
     * Newest first by MediaStore id (ids only grow, and images and videos share them), older than [beforeId]
     * when given. The Files collection returns only the media types we're allowed to read.
     */
    private const val MEDIA = "${FileColumns.MEDIA_TYPE} IN (${FileColumns.MEDIA_TYPE_IMAGE}, ${FileColumns.MEDIA_TYPE_VIDEO})"

    /** Albums (MediaStore buckets: the folders photos are in), biggest first. Only readable items count. */
    fun albums(c: Context): JSONArray {
        val counts = LinkedHashMap<String, Pair<String, Int>>()
        c.contentResolver.query(
            MediaStore.Files.getContentUri("external"),
            arrayOf(MediaStore.MediaColumns.BUCKET_ID, MediaStore.MediaColumns.BUCKET_DISPLAY_NAME), MEDIA, null, null,
        )?.use { cur ->
            while (cur.moveToNext()) {
                val id = cur.getString(0) ?: continue
                val (name, n) = counts[id] ?: ((cur.getString(1)?.takeIf { it.isNotBlank() } ?: "Other") to 0)
                counts[id] = name to n + 1
            }
        }
        return JSONArray(counts.entries.sortedByDescending { it.value.second }.take(200).map { (id, v) ->
            JSONObject().put("id", id.take(32)).put("name", v.first.take(255)).put("count", v.second)
        })
    }

    fun list(c: Context, limit: Int, beforeId: Long?, album: String? = null): JSONArray {
        val out = JSONArray()
        c.contentResolver.query(
            MediaStore.Files.getContentUri("external"),
            arrayOf(FileColumns._ID, FileColumns.DISPLAY_NAME, FileColumns.DATE_TAKEN, FileColumns.DATE_ADDED, FileColumns.WIDTH,
                FileColumns.HEIGHT, FileColumns.SIZE, FileColumns.MIME_TYPE, FileColumns.MEDIA_TYPE, FileColumns.DURATION),
            MEDIA + (beforeId?.let { " AND ${FileColumns._ID} < ?" } ?: "") +
                (album?.let { " AND ${MediaStore.MediaColumns.BUCKET_ID} = ?" } ?: ""),
            listOfNotNull(beforeId?.toString(), album).toTypedArray(), "${FileColumns._ID} DESC",
        )?.use { cur ->
            while (cur.moveToNext() && out.length() < limit) {
                val taken = cur.getLong(2)
                val video = cur.getInt(8) == FileColumns.MEDIA_TYPE_VIDEO
                out.put(JSONObject()
                    .put("id", cur.getLong(0).toString())
                    .put("date", if (taken > 0) taken else cur.getLong(3) * 1000)
                    .put("width", cur.getInt(4).coerceAtLeast(0))
                    .put("height", cur.getInt(5).coerceAtLeast(0))
                    .put("size", cur.getLong(6).coerceAtLeast(0))
                    .put("mime", (cur.getString(7) ?: if (video) "video/mp4" else "image/jpeg").take(64))
                    .apply {
                        cur.getString(1)?.let { put("name", it.take(255)) }
                        if (video) put("video", true).put("duration", cur.getLong(9).coerceAtLeast(0))
                    })
            }
        }
        return out
    }

    /** A JPEG preview about 320 px on its long side (a frame, for videos), base64. */
    fun thumbnail(c: Context, id: Long, video: Boolean): String {
        val bitmap = c.contentResolver.loadThumbnail(uri(id, video), Size(320, 320), null)
        val jpeg = ByteArrayOutputStream().also { bitmap.compress(Bitmap.CompressFormat.JPEG, 80, it) }.toByteArray()
        bitmap.recycle()
        return Base64.encodeToString(jpeg, Base64.NO_WRAP)
    }

    class Original(val name: String, val mime: String, val size: Long, val stream: InputStream)

    /** The full-size file for a transfer connection. Null if it's gone or not readable. */
    fun open(c: Context, id: Long, video: Boolean): Original? = open(c, uri(id, video), if (video) "video.mp4" else "photo.jpg")

    /** Any readable content URI with a known length (also used for things shared to Palwyn). */
    fun open(c: Context, uri: Uri, fallbackName: String): Original? {
        var name = fallbackName
        val mime = c.contentResolver.getType(uri) ?: "application/octet-stream"
        var size = -1L
        c.contentResolver.query(uri, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME, MediaStore.MediaColumns.SIZE), null, null, null)?.use {
            if (it.moveToFirst()) {
                name = it.getString(0) ?: name
                if (!it.isNull(1)) size = it.getLong(1)
            }
        }
        // The file's real length beats the (sometimes stale) SIZE column.
        c.contentResolver.openAssetFileDescriptor(uri, "r")?.use { if (it.length >= 0) size = it.length }
        if (size < 0) return null
        val stream = c.contentResolver.openInputStream(uri) ?: return null
        return Original(name.take(255), mime.take(127), size, stream)
    }
}
