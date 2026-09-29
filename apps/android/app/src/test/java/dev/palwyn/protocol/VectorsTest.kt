package dev.palwyn.protocol

import dev.palwyn.pairing.PairingCrypto
import dev.palwyn.pairing.PairingInvite
import org.json.JSONObject
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test
import java.io.File

/** The same shared vectors the Windows tests run: both platforms must agree byte for byte. */
class VectorsTest {
    private fun load(name: String) =
        JSONObject(File(System.getProperty("user.dir"), "../../../shared/protocol/$name").readText())

    @Test
    fun protocolVectors() {
        val doc = load("test-vectors.json")
        val defaults = doc.getJSONObject("defaults").getJSONArray("capabilities")
        val vectors = doc.getJSONArray("vectors")
        for (i in 0 until vectors.length()) {
            val v = vectors.getJSONObject(i)
            val capsJson = v.optJSONArray("capabilities") ?: defaults
            val caps = (0 until capsJson.length()).map { capsJson.getString(it) }.toSet()
            val receiver = if (v.getString("receiver") == "phone") Side.Phone else Side.Pc

            val actual = if (v.has("lengthPrefix")) {
                if (Envelope.lengthOk(v.getInt("lengthPrefix"))) "accept" else "close"
            } else {
                when (val verdict = Envelope.validate(v.getString("frame").toByteArray(), receiver, caps).first) {
                    Verdict.Accept -> "accept"
                    Verdict.Close -> "close"
                    is Verdict.Error -> "error:${verdict.code}"
                }
            }
            assertEquals(v.getString("name"), v.getString("expect"), actual)
        }
    }

    @Test
    fun pairingVectors() {
        val doc = load("pairing-vectors.json")
        val inputs = doc.getJSONObject("inputs")
        val expected = doc.getJSONObject("expected")
        fun input(k: String) = inputs.getString(k).unhex()

        assertEquals(expected.getString("proof"), PairingCrypto.proof(input("secret"), input("fpPc"), input("fpPhone")).hex())
        assertEquals(expected.getString("commit"), PairingCrypto.commit(input("nPc")).hex())
        assertEquals(expected.getString("code"), PairingCrypto.code(input("nPc"), input("nPhone"), input("fpPc"), input("fpPhone")))

        val invite = PairingInvite(input("fpPc"), input("secret"), inputs.getString("pcName"))
        assertEquals(expected.getString("uri"), invite.toUri())
        val parsed = PairingInvite.parse(expected.getString("uri"))!!
        assertArrayEquals(invite.pcFingerprint, parsed.pcFingerprint)
        assertArrayEquals(invite.secret, parsed.secret)
        assertEquals("DESKTOP 1", parsed.pcName)
    }

    @Test
    fun badInvitesAreRejected() {
        listOf(
            "https://example.com/pair?v=1",
            "palwyn://pair?v=2&fp=oaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaGhoaE&s=ABEiM0RVZneImaq7zN3u_w&n=x",
            "palwyn://pair?v=1&fp=AAAA&s=ABEiM0RVZneImaq7zN3u_w&n=x",
            "palwyn://pair?v=1&s=ABEiM0RVZneImaq7zN3u_w",
        ).forEach { assertNull(it, PairingInvite.parse(it)) }
    }
}
