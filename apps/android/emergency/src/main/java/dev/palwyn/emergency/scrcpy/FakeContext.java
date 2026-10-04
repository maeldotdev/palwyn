// Ported from scrcpy v4.1 (https://github.com/Genymobile/scrcpy), server/src/main/java/com/genymobile/scrcpy/FakeContext.java
// Copyright (C) 2018 Genymobile, Copyright (C) 2018-2026 Romain Vimont. Licensed under the Apache License, Version 2.0
// (third_party/scrcpy/LICENSE).
// Modified for Palwyn: package renamed; the content-provider resolver removed (Palwyn reads no content providers).
package dev.palwyn.emergency.scrcpy;

import android.annotation.TargetApi;
import android.content.AttributionSource;
import android.content.Context;
import android.content.ContextWrapper;
import android.os.Build;
import android.os.Process;

public final class FakeContext extends ContextWrapper {

    public static final String PACKAGE_NAME = "com.android.shell";

    private static final FakeContext INSTANCE = new FakeContext();

    public static FakeContext get() {
        return INSTANCE;
    }

    private FakeContext() {
        super(Workarounds.getSystemContext());
    }

    @Override
    public String getPackageName() {
        return PACKAGE_NAME;
    }

    @Override
    public String getOpPackageName() {
        return PACKAGE_NAME;
    }

    @TargetApi(Build.VERSION_CODES.S)
    @Override
    public AttributionSource getAttributionSource() {
        AttributionSource.Builder builder = new AttributionSource.Builder(Process.SHELL_UID);
        builder.setPackageName(PACKAGE_NAME);
        return builder.build();
    }

    // @Override to be added on SDK upgrade for Android 14
    @SuppressWarnings("unused")
    public int getDeviceId() {
        return 0;
    }

    @Override
    public Context getApplicationContext() {
        return this;
    }

    @Override
    public Context createPackageContext(String packageName, int flags) {
        return this;
    }
}
