package dev.palwyn.emergency;

import java.io.IOException;
import java.io.InputStream;
import java.security.MessageDigest;

/** The PC proves it started this helper: it sends the random token it passed on the command line. */
final class Handshake {
    private Handshake() {
    }

    /** Reads exactly token.length bytes; false if the stream ends early or they differ (constant-time compare). */
    static boolean check(InputStream in, byte[] token) throws IOException {
        byte[] got = new byte[token.length];
        int n = 0;
        while (n < got.length) {
            int r = in.read(got, n, got.length - n);
            if (r < 0) return false;
            n += r;
        }
        return MessageDigest.isEqual(got, token);
    }

    static byte[] fromHex(String hex) {
        byte[] b = new byte[hex.length() / 2];
        for (int i = 0; i < b.length; i++) b[i] = (byte) Integer.parseInt(hex.substring(2 * i, 2 * i + 2), 16);
        return b;
    }
}
