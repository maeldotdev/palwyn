package dev.palwyn.identity

import org.junit.Assert.assertEquals
import org.junit.Test

class FingerprintTest {
    // SHA-256("abc") = ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad
    private val abc = Fingerprint.of("abc".toByteArray())

    @Test
    fun fingerprintIsSha256OfDer() {
        assertEquals(32, abc.size)
        assertEquals(0xba.toByte(), abc[0])
        assertEquals(0xad.toByte(), abc[31])
    }

    @Test
    fun deviceIdIsFirst16BytesLowercaseHex() {
        assertEquals("ba7816bf8f01cfea414140de5dae2223", Fingerprint.deviceId(abc))
    }

    @Test
    fun displayFormIsTwoGroupsOfFour() {
        assertEquals("BA78 16BF", Fingerprint.display("ba7816bf8f01cfea414140de5dae2223"))
    }
}
