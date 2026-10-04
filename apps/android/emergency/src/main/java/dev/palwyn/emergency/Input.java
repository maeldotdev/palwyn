package dev.palwyn.emergency;

import android.os.SystemClock;
import android.view.InputDevice;
import android.view.KeyCharacterMap;
import android.view.KeyEvent;
import android.view.MotionEvent;

import org.json.JSONObject;

import dev.palwyn.emergency.scrcpy.DisplayManager;
import dev.palwyn.emergency.scrcpy.InputManager;

/**
 * Palwyn's screen controls (docs/protocol.md: SCREEN_TOUCH, SCREEN_KEY, SCREEN_TEXT), injected as the shell user, so
 * they also reach the lock screen. One JSON object per line: {"type": ..., "payload": {...}}. Unknown types are ignored.
 */
final class Input {
    private static final int STEP_MS = 16;

    void handle(String line) throws Exception {
        JSONObject o = new JSONObject(line);
        JSONObject p = o.optJSONObject("payload");
        if (p == null) return;
        switch (o.optString("type")) {
            case "SCREEN_TOUCH":
                touch(p.getInt("x1"), p.getInt("y1"), p.getInt("x2"), p.getInt("y2"), p.getInt("ms"));
                break;
            case "SCREEN_KEY":
                int code = keyCode(p.optString("key"));
                if (code != KeyEvent.KEYCODE_UNKNOWN) key(code);
                break;
            case "SCREEN_TEXT":
                text(p.optString("text"));
                break;
            default:
                break;
        }
    }

    /** Turns the screen on, so the PC sees the lock screen rather than black. */
    void wake() {
        key(KeyEvent.KEYCODE_WAKEUP);
    }

    private static int keyCode(String key) {
        switch (key) {
            case "back": return KeyEvent.KEYCODE_BACK;
            case "home": return KeyEvent.KEYCODE_HOME;
            case "recents": return KeyEvent.KEYCODE_APP_SWITCH;
            case "backspace": return KeyEvent.KEYCODE_DEL;
            case "enter": return KeyEvent.KEYCODE_ENTER;
            default: return KeyEvent.KEYCODE_UNKNOWN;
        }
    }

    /** From (x1, y1) to (x2, y2), 0-10000 across the screen as shown, over ms: a tap, long press or straight swipe. */
    private void touch(int x1, int y1, int x2, int y2, int ms) throws InterruptedException {
        DisplayManager.DisplayInfo d = DisplayManager.get().getDisplayInfo(0);
        ms = Math.max(1, Math.min(10000, ms));
        float ax = px(x1, d.width), ay = px(y1, d.height), bx = px(x2, d.width), by = px(y2, d.height);
        long down = SystemClock.uptimeMillis();
        motion(down, down, MotionEvent.ACTION_DOWN, ax, ay);
        int steps = Math.max(1, ms / STEP_MS);
        for (int i = 1; i < steps; i++) {
            Thread.sleep(STEP_MS);
            float f = (float) i / steps;
            motion(down, SystemClock.uptimeMillis(), MotionEvent.ACTION_MOVE, ax + (bx - ax) * f, ay + (by - ay) * f);
        }
        Thread.sleep(Math.max(1, ms - (steps - 1) * STEP_MS));
        motion(down, SystemClock.uptimeMillis(), MotionEvent.ACTION_UP, bx, by);
    }

    private static float px(int v, int size) {
        return Math.max(0, Math.min(10000, v)) / 10000f * (size - 1);
    }

    private static void motion(long downTime, long time, int action, float x, float y) {
        MotionEvent e = MotionEvent.obtain(downTime, time, action, x, y, 0);
        e.setSource(InputDevice.SOURCE_TOUCHSCREEN);
        InputManager.get().injectInputEvent(e, InputManager.INJECT_INPUT_EVENT_MODE_ASYNC);
        e.recycle();
    }

    private static void key(int code) {
        long now = SystemClock.uptimeMillis();
        for (int action : new int[] {KeyEvent.ACTION_DOWN, KeyEvent.ACTION_UP}) {
            KeyEvent e = new KeyEvent(now, now, action, code, 0, 0, KeyCharacterMap.VIRTUAL_KEYBOARD, 0, 0, InputDevice.SOURCE_KEYBOARD);
            InputManager.get().injectInputEvent(e, InputManager.INJECT_INPUT_EVENT_MODE_ASYNC);
        }
    }

    /** Typed as key events (works in any field, including the lock screen's PIN pad); characters with no key are skipped. */
    private static void text(String text) {
        if (text.length() > 1000) text = text.substring(0, 1000);
        KeyCharacterMap map = KeyCharacterMap.load(KeyCharacterMap.VIRTUAL_KEYBOARD);
        for (char c : text.toCharArray()) {
            KeyEvent[] events = map.getEvents(new char[] {c});
            if (events == null) continue;
            for (KeyEvent e : events) InputManager.get().injectInputEvent(e, InputManager.INJECT_INPUT_EVENT_MODE_ASYNC);
        }
    }
}
