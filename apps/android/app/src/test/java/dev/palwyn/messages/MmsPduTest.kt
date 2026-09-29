package dev.palwyn.messages

import dev.palwyn.messages.MmsPdu.uintvar
import org.junit.Assert.assertArrayEquals
import org.junit.Test
import java.io.ByteArrayOutputStream

class MmsPduTest {
    private fun bytes(vararg parts: Any): ByteArray = ByteArrayOutputStream().apply {
        for (p in parts) when (p) {
            is Int -> write(p)
            is String -> write(p.toByteArray(Charsets.US_ASCII))
        }
    }.toByteArray()

    @Test
    fun textSendReq() {
        val pdu = MmsPdu.sendReq(listOf("+63 917 123 4567", "0917-000-1111"), "Hi", "T1", 0x5F000000)
        val expected = bytes(
            0x8C, 0x80, 0x98, "T1", 0, 0x8D, 0x92, 0x85, 4, 0x5F, 0, 0, 0, 0x89, 1, 0x81,
            0x97, "+639171234567/TYPE=PLMN", 0, 0x97, "09170001111/TYPE=PLMN", 0,
            0x84, 0xA3, 1, 4, 2, 0x03, 0x83, 0x81, 0xEA, "Hi",
        )
        assertArrayEquals(expected, pdu)
    }

    @Test
    fun uintvars() {
        fun enc(v: Int) = ByteArrayOutputStream().apply { uintvar(v) }.toByteArray()
        assertArrayEquals(bytes(0), enc(0))
        assertArrayEquals(bytes(0x7F), enc(127))
        assertArrayEquals(bytes(0x81, 0x48), enc(200))
        assertArrayEquals(bytes(0x81, 0x80, 0x00), enc(16384))
    }
}
