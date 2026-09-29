package dev.palwyn.device

import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothManager
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.BatteryManager
import android.os.Build
import android.os.Environment
import android.os.StatFs
import android.provider.Settings
import android.Manifest
import android.content.pm.PackageManager
import android.telephony.PhoneStateListener
import android.telephony.SignalStrength
import android.telephony.TelephonyManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/** What Palwyn will share with a paired PC about this phone (DEVICE_INFO / BATTERY_CHANGED). */
data class PhoneSnapshot(
    val name: String,
    val manufacturer: String,
    val model: String,
    val androidVersion: String,
    val sdk: Int,
    val batteryPercent: Int?,
    val charging: Boolean,
    val storageFreeBytes: Long,
    val storageTotalBytes: Long,
    val onWifi: Boolean,
    /** 0..4 bars, null when not on Wi-Fi or unknown. */
    val wifiSignal: Int? = null,
    /** null when the phone has no Bluetooth. */
    val bluetooth: Boolean? = null,
    /** Mobile network: 0..4 bars, null without a usable SIM. */
    val cellSignal: Int? = null,
    /** "5G", "LTE", "3G" or "2G"; null when unknown (needs READ_PHONE_STATE). */
    val cellNetwork: String? = null,
    val carrier: String? = null,
)

/** Event-driven: battery broadcasts and network callbacks, no polling. Runs while the link service does. */
object PhoneMonitor {
    private val _snapshot = MutableStateFlow<PhoneSnapshot?>(null)
    val snapshot: StateFlow<PhoneSnapshot?> = _snapshot

    private var battery: BroadcastReceiver? = null
    private var network: ConnectivityManager.NetworkCallback? = null
    private var bluetooth: BroadcastReceiver? = null
    private var cell: PhoneStateListener? = null

    fun init(c: Context) {
        _snapshot.value = read(c.applicationContext)
    }

    fun start(c: Context) {
        if (battery != null) return
        val app = c.applicationContext
        _snapshot.value = read(app)

        battery = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = update {
                it.copy(batteryPercent = batteryPercent(intent), charging = charging(intent))
            }
        }.also { app.registerReceiver(it, IntentFilter(Intent.ACTION_BATTERY_CHANGED)) }

        network = object : ConnectivityManager.NetworkCallback() {
            override fun onCapabilitiesChanged(n: Network, caps: NetworkCapabilities) = update {
                it.copy(onWifi = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI), wifiSignal = wifiSignal(caps))
            }

            override fun onLost(n: Network) = update { it.copy(onWifi = false, wifiSignal = null) }
        }.also { app.getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(it) }

        bluetooth = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) = update {
                it.copy(bluetooth = intent.getIntExtra(BluetoothAdapter.EXTRA_STATE, -1) == BluetoothAdapter.STATE_ON)
            }
        }.also { app.registerReceiver(it, IntentFilter(BluetoothAdapter.ACTION_STATE_CHANGED)) }

        // Signal changes arrive as events; the network type and carrier are read with them. No permission for the bars.
        @Suppress("DEPRECATION")
        cell = object : PhoneStateListener(app.mainExecutor) {
            override fun onSignalStrengthsChanged(s: SignalStrength) = update { cellular(app, s, it) }
        }.also { app.getSystemService(TelephonyManager::class.java)?.listen(it, PhoneStateListener.LISTEN_SIGNAL_STRENGTHS) }
    }

    fun stop(c: Context) {
        val app = c.applicationContext
        battery?.let { app.unregisterReceiver(it) }
        bluetooth?.let { app.unregisterReceiver(it) }
        network?.let { app.getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(it) }
        @Suppress("DEPRECATION")
        cell?.let { app.getSystemService(TelephonyManager::class.java)?.listen(it, PhoneStateListener.LISTEN_NONE) }
        cell = null
        battery = null
        bluetooth = null
        network = null
    }

    /** Free space changes without any event; call after writing a file. */
    fun refreshStorage() = update { it.copy(storageFreeBytes = StatFs(Environment.getDataDirectory().path).availableBytes) }

    // RSSI to 0..4 bars, the way the status bar roughly does. No permission needed (the SSID would need location).
    private fun wifiSignal(caps: NetworkCapabilities?): Int? {
        if (caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) != true) return null
        val rssi = caps.signalStrength.takeIf { it != NetworkCapabilities.SIGNAL_STRENGTH_UNSPECIFIED } ?: return null
        return when {
            rssi >= -55 -> 4
            rssi >= -66 -> 3
            rssi >= -77 -> 2
            rssi >= -88 -> 1
            else -> 0
        }
    }

    /** Bars, network type and carrier of the mobile network; all null without a ready SIM. */
    private fun cellular(c: Context, s: SignalStrength?, base: PhoneSnapshot): PhoneSnapshot {
        val tm = c.getSystemService(TelephonyManager::class.java)
        if (tm == null || tm.simState != TelephonyManager.SIM_STATE_READY) return base.copy(cellSignal = null, cellNetwork = null, carrier = null)
        val network = if (c.checkSelfPermission(Manifest.permission.READ_PHONE_STATE) != PackageManager.PERMISSION_GRANTED) null
            else when (tm.dataNetworkType) {
                TelephonyManager.NETWORK_TYPE_NR -> "5G"
                TelephonyManager.NETWORK_TYPE_LTE -> "LTE"
                TelephonyManager.NETWORK_TYPE_UMTS, TelephonyManager.NETWORK_TYPE_HSDPA, TelephonyManager.NETWORK_TYPE_HSUPA,
                TelephonyManager.NETWORK_TYPE_HSPA, TelephonyManager.NETWORK_TYPE_HSPAP, TelephonyManager.NETWORK_TYPE_TD_SCDMA -> "3G"
                TelephonyManager.NETWORK_TYPE_GSM, TelephonyManager.NETWORK_TYPE_EDGE, TelephonyManager.NETWORK_TYPE_GPRS -> "2G"
                else -> null
            }
        return base.copy(
            cellSignal = (s ?: tm.signalStrength)?.level?.coerceIn(0, 4),
            cellNetwork = network,
            carrier = tm.networkOperatorName?.takeIf { it.isNotBlank() }?.take(64),
        )
    }

    private fun update(change: (PhoneSnapshot) -> PhoneSnapshot) = _snapshot.update { it?.let(change) }

    private fun read(c: Context): PhoneSnapshot {
        val sticky = c.registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val stat = StatFs(Environment.getDataDirectory().path)
        val cm = c.getSystemService(ConnectivityManager::class.java)
        val caps = cm.getNetworkCapabilities(cm.activeNetwork)
        return cellular(c, null, PhoneSnapshot(
            name = Settings.Global.getString(c.contentResolver, Settings.Global.DEVICE_NAME) ?: Build.MODEL,
            manufacturer = Build.MANUFACTURER, // keep the brand's own casing ("realme", "OnePlus")
            model = Build.MODEL,
            androidVersion = Build.VERSION.RELEASE,
            sdk = Build.VERSION.SDK_INT,
            batteryPercent = sticky?.let(::batteryPercent),
            charging = sticky?.let(::charging) ?: false,
            storageFreeBytes = stat.availableBytes,
            storageTotalBytes = stat.totalBytes,
            onWifi = caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true,
            wifiSignal = wifiSignal(caps),
            // isEnabled needs the install-time BLUETOOTH permission only up to Android 11.
            bluetooth = c.getSystemService(BluetoothManager::class.java)?.adapter?.isEnabled,
        ))
    }

    private fun batteryPercent(i: Intent): Int? {
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        return if (level >= 0 && scale > 0) level * 100 / scale else null
    }

    private fun charging(i: Intent) = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1).let {
        it == BatteryManager.BATTERY_STATUS_CHARGING || it == BatteryManager.BATTERY_STATUS_FULL
    }
}
