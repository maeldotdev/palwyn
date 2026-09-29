package dev.palwyn.link

import android.content.Context
import dev.palwyn.protocol.hex
import org.json.JSONArray
import org.json.JSONObject

data class PairedPc(val fingerprint: String, val name: String, val pairedAt: Long)

/** PCs this phone trusts, pinned by certificate fingerprint (docs/security.md section 4). */
object PairedPcs {
    private fun prefs(c: Context) = c.getSharedPreferences("paired", Context.MODE_PRIVATE)

    @Synchronized
    fun all(c: Context): List<PairedPc> {
        val json = JSONArray(prefs(c).getString("pcs", "[]"))
        return (0 until json.length()).map { json.getJSONObject(it) }.map {
            PairedPc(it.getString("fp"), it.getString("name"), it.getLong("pairedAt"))
        }
    }

    fun contains(c: Context, fingerprint: ByteArray) = all(c).any { it.fingerprint == fingerprint.hex() }

    fun find(c: Context, fingerprint: ByteArray) = all(c).firstOrNull { it.fingerprint == fingerprint.hex() }

    @Synchronized
    fun save(c: Context, fingerprint: ByteArray, name: String) {
        val existing = find(c, fingerprint)
        write(c, all(c).filterNot { it.fingerprint == fingerprint.hex() } +
            PairedPc(fingerprint.hex(), name, existing?.pairedAt ?: System.currentTimeMillis()))
    }

    @Synchronized
    fun remove(c: Context, fingerprintHex: String) = write(c, all(c).filterNot { it.fingerprint == fingerprintHex })

    private fun write(c: Context, list: List<PairedPc>) {
        val json = JSONArray(list.map {
            JSONObject().put("fp", it.fingerprint).put("name", it.name).put("pairedAt", it.pairedAt)
        })
        prefs(c).edit().putString("pcs", json.toString()).apply()
    }
}
