package dev.palwyn.messages

import java.io.ByteArrayOutputStream

/**
 * A text-only MMS m-send-req PDU (OMA-TS-MMS_ENC, WAP-230 WSP encoding), which SmsManager.sendMultimediaMessage
 * takes as a file. Android has no public PDU composer, so this writes the few headers a send needs.
 */
object MmsPdu {
    fun sendReq(recipients: List<String>, text: String, transactionId: String, dateSeconds: Long): ByteArray {
        require(recipients.isNotEmpty())
        val out = ByteArrayOutputStream()
        out.write(0x8C); out.write(0x80) // X-Mms-Message-Type: m-send-req
        out.write(0x98); out.textString(transactionId) // X-Mms-Transaction-Id
        out.write(0x8D); out.write(0x92) // X-Mms-MMS-Version: 1.2
        out.write(0x85); out.write(4) // Date: long-integer, 4 octets
        for (shift in intArrayOf(24, 16, 8, 0)) out.write((dateSeconds ushr shift).toInt() and 0xFF)
        out.write(0x89); out.write(0x01); out.write(0x81) // From: insert-address-token (the network fills it in)
        for (r in recipients) {
            out.write(0x97) // To
            out.textString("${r.filter { it.isDigit() || it == '+' }}/TYPE=PLMN")
        }
        out.write(0x84); out.write(0xA3) // Content-Type: application/vnd.wap.multipart.mixed
        // Body: one part, text/plain; charset=utf-8.
        val data = text.toByteArray(Charsets.UTF_8)
        val headers = byteArrayOf(0x03, 0x83.toByte(), 0x81.toByte(), 0xEA.toByte()) // value-length 3, text/plain, charset, utf-8 (106)
        out.uintvar(1)
        out.uintvar(headers.size)
        out.uintvar(data.size)
        out.write(headers)
        out.write(data)
        return out.toByteArray()
    }

    private fun ByteArrayOutputStream.textString(s: String) {
        val bytes = s.toByteArray(Charsets.US_ASCII)
        if (bytes.isNotEmpty() && bytes[0].toInt() and 0x80 != 0) write(0x7F) // quote a first byte that looks like a token
        write(bytes)
        write(0)
    }

    /** WSP variable-length unsigned integer: 7 bits per byte, most significant first, high bit = more follow. */
    internal fun ByteArrayOutputStream.uintvar(value: Int) {
        var shift = 28
        while (shift > 0 && value ushr shift == 0) shift -= 7
        while (shift > 0) {
            write(((value ushr shift) and 0x7F) or 0x80)
            shift -= 7
        }
        write(value and 0x7F)
    }
}
