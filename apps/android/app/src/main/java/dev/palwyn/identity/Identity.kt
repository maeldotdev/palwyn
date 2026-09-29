package dev.palwyn.identity

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.math.BigInteger
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.X509Certificate
import java.security.spec.ECGenParameterSpec
import java.util.Calendar
import java.util.Date
import javax.security.auth.x500.X500Principal

/** Fingerprint rules shared with Windows (docs/security.md §3). Pure, so it runs in JVM tests. */
object Fingerprint {
    fun of(certificateDer: ByteArray): ByteArray = MessageDigest.getInstance("SHA-256").digest(certificateDer)

    /** deviceId = first 16 bytes of the fingerprint, lowercase hex. */
    fun deviceId(fingerprint: ByteArray): String = fingerprint.copyOf(16).joinToString("") { "%02x".format(it) }

    /** Short form for people: "BA78 16BF". */
    fun display(deviceId: String): String = deviceId.take(8).uppercase().chunked(4).joinToString(" ")
}

/**
 * This install's identity: an EC P-256 key generated inside Android Keystore (never exportable) and the
 * self-signed certificate Keystore issues for it. Trust comes from pinning the fingerprint, not from a CA.
 * Slow on first use (key generation); call off the main thread.
 */
object DeviceIdentity {
    const val ALIAS = "palwyn-identity"

    fun certificate(): X509Certificate {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        if (!ks.containsAlias(ALIAS)) generate()
        return ks.getCertificate(ALIAS) as X509Certificate
    }

    fun deviceId(): String = Fingerprint.deviceId(Fingerprint.of(certificate().encoded))

    private fun generate() {
        val notAfter = Calendar.getInstance().apply { add(Calendar.YEAR, 20) }.time
        val spec = KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
            .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1"))
            .setDigests(KeyProperties.DIGEST_SHA256, KeyProperties.DIGEST_NONE)
            .setCertificateSubject(X500Principal("CN=Palwyn"))
            .setCertificateSerialNumber(BigInteger(63, SecureRandom()))
            .setCertificateNotBefore(Date())
            .setCertificateNotAfter(notAfter)
            .build()
        KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, "AndroidKeyStore").run {
            initialize(spec)
            generateKeyPair()
        }
    }
}
