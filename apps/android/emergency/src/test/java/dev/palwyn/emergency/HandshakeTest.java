package dev.palwyn.emergency;

import static org.junit.Assert.assertFalse;
import static org.junit.Assert.assertTrue;

import java.io.ByteArrayInputStream;

import org.junit.Test;

public class HandshakeTest {
    private static final byte[] T = new byte[32];

    static {
        for (int i = 0; i < 32; i++) T[i] = (byte) i;
    }

    @Test
    public void acceptsExactToken() throws Exception {
        assertTrue(Handshake.check(new ByteArrayInputStream(T), T));
    }

    @Test
    public void rejectsWrongToken() throws Exception {
        byte[] w = T.clone();
        w[31] ^= 1;
        assertFalse(Handshake.check(new ByteArrayInputStream(w), T));
    }

    @Test
    public void rejectsShortToken() throws Exception {
        assertFalse(Handshake.check(new ByteArrayInputStream(new byte[16]), T));
    }

    @Test
    public void parsesHexToken() {
        byte[] t = Handshake.fromHex("000102030405060708090a0b0c0d0e0f101112131415161718191a1b1c1d1e1f");
        assertTrue(java.util.Arrays.equals(T, t));
    }
}
