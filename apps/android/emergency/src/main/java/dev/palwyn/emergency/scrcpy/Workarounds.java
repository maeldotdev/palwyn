// Ported from scrcpy v4.1 (https://github.com/Genymobile/scrcpy), server/src/main/java/com/genymobile/scrcpy/Workarounds.java
// Copyright (C) 2018 Genymobile, Copyright (C) 2018-2026 Romain Vimont. Licensed under the Apache License, Version 2.0
// (third_party/scrcpy/LICENSE).
// Modified for Palwyn: package renamed; only the ActivityThread setup and the system context kept (no audio parts);
// logging through System.err.
package dev.palwyn.emergency.scrcpy;

import android.annotation.SuppressLint;
import android.app.Application;
import android.app.Instrumentation;
import android.content.Context;
import android.content.pm.ApplicationInfo;
import android.os.Build;

import java.lang.reflect.Constructor;
import java.lang.reflect.Field;
import java.lang.reflect.Method;

@SuppressLint("PrivateApi,BlockedPrivateApi,SoonBlockedPrivateApi,DiscouragedPrivateApi")
public final class Workarounds {

    private static final Class<?> ACTIVITY_THREAD_CLASS;
    private static final Object ACTIVITY_THREAD;

    static {
        try {
            // ActivityThread activityThread = new ActivityThread();
            ACTIVITY_THREAD_CLASS = Class.forName("android.app.ActivityThread");
            Constructor<?> activityThreadConstructor = ACTIVITY_THREAD_CLASS.getDeclaredConstructor();
            activityThreadConstructor.setAccessible(true);
            ACTIVITY_THREAD = activityThreadConstructor.newInstance();

            // ActivityThread.sCurrentActivityThread = activityThread;
            Field sCurrentActivityThreadField = ACTIVITY_THREAD_CLASS.getDeclaredField("sCurrentActivityThread");
            sCurrentActivityThreadField.setAccessible(true);
            sCurrentActivityThreadField.set(null, ACTIVITY_THREAD);

            // activityThread.mSystemThread = true;
            Field mSystemThreadField = ACTIVITY_THREAD_CLASS.getDeclaredField("mSystemThread");
            mSystemThreadField.setAccessible(true);
            mSystemThreadField.setBoolean(ACTIVITY_THREAD, true);
        } catch (Exception e) {
            throw new AssertionError(e);
        }
    }

    private Workarounds() {
        // not instantiable
    }

    public static void apply() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            // On some Samsung devices, DisplayManagerGlobal.getDisplayInfoLocked() calls ActivityThread.currentActivityThread().getConfiguration(),
            // which requires a non-null ConfigurationController.
            // Must be called before fillAppContext() because it is necessary to get a valid system context.
            fillConfigurationController();
        }

        // On ONYX devices, fillAppInfo() breaks video mirroring: <https://github.com/Genymobile/scrcpy/issues/5182>
        if (!Build.BRAND.equalsIgnoreCase("ONYX")) {
            fillAppInfo();
        }

        fillAppContext();
    }

    private static void fillAppInfo() {
        try {
            // ActivityThread.AppBindData appBindData = new ActivityThread.AppBindData();
            Class<?> appBindDataClass = Class.forName("android.app.ActivityThread$AppBindData");
            Constructor<?> appBindDataConstructor = appBindDataClass.getDeclaredConstructor();
            appBindDataConstructor.setAccessible(true);
            Object appBindData = appBindDataConstructor.newInstance();

            ApplicationInfo applicationInfo = new ApplicationInfo();
            applicationInfo.packageName = FakeContext.PACKAGE_NAME;

            // appBindData.appInfo = applicationInfo;
            Field appInfoField = appBindDataClass.getDeclaredField("appInfo");
            appInfoField.setAccessible(true);
            appInfoField.set(appBindData, applicationInfo);

            // activityThread.mBoundApplication = appBindData;
            Field mBoundApplicationField = ACTIVITY_THREAD_CLASS.getDeclaredField("mBoundApplication");
            mBoundApplicationField.setAccessible(true);
            mBoundApplicationField.set(ACTIVITY_THREAD, appBindData);
        } catch (Throwable throwable) {
            // this is a workaround, so failing is not an error
            System.err.println("Could not fill app info: " + throwable.getMessage());
        }
    }

    private static void fillAppContext() {
        try {
            Application app = Instrumentation.newApplication(Application.class, FakeContext.get());

            // activityThread.mInitialApplication = app;
            Field mInitialApplicationField = ACTIVITY_THREAD_CLASS.getDeclaredField("mInitialApplication");
            mInitialApplicationField.setAccessible(true);
            mInitialApplicationField.set(ACTIVITY_THREAD, app);
        } catch (Throwable throwable) {
            // this is a workaround, so failing is not an error
            System.err.println("Could not fill app context: " + throwable.getMessage());
        }
    }

    private static void fillConfigurationController() {
        try {
            Class<?> configurationControllerClass = Class.forName("android.app.ConfigurationController");
            Class<?> activityThreadInternalClass = Class.forName("android.app.ActivityThreadInternal");

            // configurationController = new ConfigurationController(ACTIVITY_THREAD);
            Constructor<?> configurationControllerConstructor = configurationControllerClass.getDeclaredConstructor(activityThreadInternalClass);
            configurationControllerConstructor.setAccessible(true);
            Object configurationController = configurationControllerConstructor.newInstance(ACTIVITY_THREAD);

            // ACTIVITY_THREAD.mConfigurationController = configurationController;
            Field configurationControllerField = ACTIVITY_THREAD_CLASS.getDeclaredField("mConfigurationController");
            configurationControllerField.setAccessible(true);
            configurationControllerField.set(ACTIVITY_THREAD, configurationController);
        } catch (Throwable throwable) {
            System.err.println("Could not fill configuration: " + throwable.getMessage());
        }
    }

    static Context getSystemContext() {
        try {
            Method getSystemContextMethod = ACTIVITY_THREAD_CLASS.getDeclaredMethod("getSystemContext");
            return (Context) getSystemContextMethod.invoke(ACTIVITY_THREAD);
        } catch (Throwable throwable) {
            // this is a workaround, so failing is not an error
            System.err.println("Could not get system context: " + throwable.getMessage());
            return null;
        }
    }
}
