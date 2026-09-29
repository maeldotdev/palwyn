package dev.palwyn.pairing

import java.net.URLDecoder
import java.net.URLEncoder
import java.security.MessageDigest
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

/** Pairing maths (docs/security.md section 5, shared/protocol/pairing-vectors.json). Must match C#. */
object PairingCrypto {
    fun proof(secret: ByteArray, fpPc: ByteArray, fpPhone: ByteArray) =
        hmac(secret, "PB-PAIR-1".toByteArray(Charsets.US_ASCII) + fpPc + fpPhone)

    fun commit(nonce: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(nonce)

    fun code(nPc: ByteArray, nPhone: ByteArray, fpPc: ByteArray, fpPhone: ByteArray): String {
        val m = hmac(nPc + nPhone, "PB-SAS-1".toByteArray(Charsets.US_ASCII) + fpPc + fpPhone)
        val u = ((m[0].toLong() and 0xff) shl 24) or ((m[1].toLong() and 0xff) shl 16) or
            ((m[2].toLong() and 0xff) shl 8) or (m[3].toLong() and 0xff)
        return "%06d".format((u and 0x7FFFFFFF) % 1_000_000)
    }

    fun same(a: ByteArray, b: ByteArray) = MessageDigest.isEqual(a, b)

    private fun hmac(key: ByteArray, data: ByteArray): ByteArray =
        Mac.getInstance("HmacSHA256").run {
            init(SecretKeySpec(key, "HmacSHA256"))
            doFinal(data)
        }
}

/** The PC's QR code: its certificate fingerprint, a one-time secret and its name. */
class PairingInvite(val pcFingerprint: ByteArray, val secret: ByteArray, val pcName: String) {
    fun toUri() = "palwyn://pair?v=1&fp=${b64(pcFingerprint)}&s=${b64(secret)}&n=" +
        URLEncoder.encode(pcName, "UTF-8").replace("+", "%20")

    override fun toString() = "PairingInvite($pcName)" // never print the secret

    companion object {
        private const val PREFIX = "palwyn://pair?"
        private fun b64(b: ByteArray) = Base64.getUrlEncoder().withoutPadding().encodeToString(b)

        fun parse(uri: String): PairingInvite? {
            if (!uri.startsWith(PREFIX)) return null
            val q = uri.removePrefix(PREFIX).split('&').mapNotNull {
                it.split('=', limit = 2).takeIf { kv -> kv.size == 2 }?.let { kv -> kv[0] to kv[1] }
            }.toMap()
            return try {
                if (q["v"] != "1") return null
                val fp = Base64.getUrlDecoder().decode(q["fp"] ?: return null)
                val secret = Base64.getUrlDecoder().decode(q["s"] ?: return null)
                if (fp.size != 32 || secret.size != 16) return null
                PairingInvite(fp, secret, URLDecoder.decode(q["n"] ?: "", "UTF-8").take(64))
            } catch (e: IllegalArgumentException) {
                null
            }
        }
    }
}
