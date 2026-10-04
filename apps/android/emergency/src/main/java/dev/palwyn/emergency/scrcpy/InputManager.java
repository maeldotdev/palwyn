// Ported from scrcpy v4.1 (https://github.com/Genymobile/scrcpy), server/src/main/java/com/genymobile/scrcpy/wrappers/InputManager.java
// Copyright (C) 2018 Genymobile, Copyright (C) 2018-2026 Romain Vimont. Licensed under the Apache License, Version 2.0
// (third_party/scrcpy/LICENSE).
// Modified for Palwyn: package renamed; kept injectInputEvent() only (no display ids, action buttons or port
// associations); created on first use instead of through ServiceManager.
package dev.palwyn.emergency.scrcpy;

import android.annotation.SuppressLint;
import android.content.Context;
import android.view.InputEvent;

import java.lang.reflect.Method;

@SuppressLint("PrivateApi,DiscouragedPrivateApi")
public final class InputManager {

    public static final int INJECT_INPUT_EVENT_MODE_ASYNC = 0;

    private static InputManager instance;
    private static Method injectInputEventMethod;

    private final android.hardware.input.InputManager manager;

    public static synchronized InputManager get() {
        if (instance == null) {
            instance = new InputManager((android.hardware.input.InputManager) FakeContext.get().getSystemService(Context.INPUT_SERVICE));
        }
        return instance;
    }

    private InputManager(android.hardware.input.InputManager manager) {
        this.manager = manager;
    }

    private static Method getInjectInputEventMethod() throws NoSuchMethodException {
        if (injectInputEventMethod == null) {
            injectInputEventMethod = android.hardware.input.InputManager.class.getMethod("injectInputEvent", InputEvent.class, int.class);
        }
        return injectInputEventMethod;
    }

    public boolean injectInputEvent(InputEvent inputEvent, int mode) {
        try {
            Method method = getInjectInputEventMethod();
            return (boolean) method.invoke(manager, inputEvent, mode);
        } catch (ReflectiveOperationException e) {
            System.err.println("Could not inject input event: " + e);
            return false;
        }
    }
}
