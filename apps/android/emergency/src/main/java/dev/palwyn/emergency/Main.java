package dev.palwyn.emergency;

import android.net.LocalServerSocket;
import android.net.LocalSocket;
import android.os.Looper;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.nio.charset.StandardCharsets;

import dev.palwyn.emergency.scrcpy.Workarounds;

/**
 * The emergency-screen helper, started by Palwyn on the PC through adb:
 * {@code CLASSPATH=/data/local/tmp/palwyn-emergency.jar app_process / dev.palwyn.emergency.Main <socketName> <tokenHex>}.
 * Serves one client on an abstract socket: the client must first send the 32-byte token, then gets screen frames
 * ({@link Capture}) and sends control lines ({@link Input}). Exits when the client goes away.
 */
public final class Main {
    private Main() {
    }

    public static void main(String[] args) {
        if (args.length != 2 || args[1].length() != 64) {
            System.err.println("usage: Main <socketName> <tokenHex>");
            System.exit(2);
        }
        byte[] token = Handshake.fromHex(args[1]);
        Looper.prepareMainLooper();
        Workarounds.apply();
        try (LocalServerSocket server = new LocalServerSocket(args[0]); LocalSocket socket = server.accept()) {
            socket.setSoTimeout(5000);
            InputStream in = socket.getInputStream();
            if (!Handshake.check(in, token)) {
                System.err.println("Wrong token");
                System.exit(3);
            }
            socket.setSoTimeout(0);
            Input input = new Input();
            input.wake();
            Thread capture = new Thread(new Capture(socket.getOutputStream()), "capture");
            capture.setDaemon(true);
            capture.start();
            BufferedReader lines = new BufferedReader(new InputStreamReader(in, StandardCharsets.UTF_8));
            String line;
            while ((line = lines.readLine()) != null) {
                try {
                    input.handle(line);
                } catch (Exception e) {
                    System.err.println("Bad control line: " + e);
                }
            }
        } catch (IOException e) {
            System.err.println("Emergency helper: " + e);
        }
        System.exit(0);
    }
}
