// Ported from scrcpy v4.1 (https://github.com/Genymobile/scrcpy), server/src/main/java/com/genymobile/scrcpy/wrappers/DisplayManager.java
// Copyright (C) 2018 Genymobile, Copyright (C) 2018-2026 Romain Vimont. Licensed under the Apache License, Version 2.0
// (third_party/scrcpy/LICENSE).
// Modified for Palwyn: package renamed; kept getDisplayInfo() and createVirtualDisplay() only (no dumpsys fallback,
// display listener, power or new-display APIs); DisplayInfo trimmed to size, rotation and layer stack.
package dev.palwyn.emergency.scrcpy;

import android.annotation.SuppressLint;
import android.hardware.display.VirtualDisplay;
import android.view.Surface;

import java.lang.reflect.Method;

@SuppressLint("PrivateApi,DiscouragedPrivateApi")
public final class DisplayManager {

    /** What Palwyn needs of android.view.DisplayInfo: logical size (rotation applied), rotation, layer stack. */
    public static final class DisplayInfo {
        public final int width;
        public final int height;
        public final int rotation;
        public final int layerStack;

        DisplayInfo(int width, int height, int rotation, int layerStack) {
            this.width = width;
            this.height = height;
            this.rotation = rotation;
            this.layerStack = layerStack;
        }
    }

    private static DisplayManager instance;

    private final Object manager; // instance of hidden class android.hardware.display.DisplayManagerGlobal
    private Method getDisplayInfoMethod;
    private Method createVirtualDisplayMethod;

    public static synchronized DisplayManager get() {
        if (instance == null) {
            try {
                Class<?> clazz = Class.forName("android.hardware.display.DisplayManagerGlobal");
                Method getInstanceMethod = clazz.getDeclaredMethod("getInstance");
                instance = new DisplayManager(getInstanceMethod.invoke(null));
            } catch (ReflectiveOperationException e) {
                throw new AssertionError(e);
            }
        }
        return instance;
    }

    private DisplayManager(Object manager) {
        this.manager = manager;
    }

    // getDisplayInfo() may be used from both the input thread and the capture thread
    private synchronized Method getGetDisplayInfoMethod() throws NoSuchMethodException {
        if (getDisplayInfoMethod == null) {
            getDisplayInfoMethod = manager.getClass().getMethod("getDisplayInfo", int.class);
        }
        return getDisplayInfoMethod;
    }

    public DisplayInfo getDisplayInfo(int displayId) {
        try {
            Object displayInfo = getGetDisplayInfoMethod().invoke(manager, displayId);
            if (displayInfo == null) {
                throw new IllegalStateException("No display " + displayId);
            }
            Class<?> cls = displayInfo.getClass();
            // width and height already take the rotation into account
            int width = cls.getDeclaredField("logicalWidth").getInt(displayInfo);
            int height = cls.getDeclaredField("logicalHeight").getInt(displayInfo);
            int rotation = cls.getDeclaredField("rotation").getInt(displayInfo);
            int layerStack = cls.getDeclaredField("layerStack").getInt(displayInfo);
            return new DisplayInfo(width, height, rotation, layerStack);
        } catch (ReflectiveOperationException e) {
            throw new AssertionError(e);
        }
    }

    private Method getCreateVirtualDisplayMethod() throws NoSuchMethodException {
        if (createVirtualDisplayMethod == null) {
            createVirtualDisplayMethod = android.hardware.display.DisplayManager.class
                    .getMethod("createVirtualDisplay", String.class, int.class, int.class, int.class, Surface.class);
        }
        return createVirtualDisplayMethod;
    }

    /** A display mirroring displayIdToMirror at width x height (hidden static API, Android 14+ for the shell). */
    public VirtualDisplay createVirtualDisplay(String name, int width, int height, int displayIdToMirror, Surface surface) throws Exception {
        Method method = getCreateVirtualDisplayMethod();
        return (VirtualDisplay) method.invoke(null, name, width, height, displayIdToMirror, surface);
    }
}
