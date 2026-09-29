package dev.palwyn.link

import android.content.Context
import android.net.nsd.NsdManager
import android.net.nsd.NsdServiceInfo
import android.util.Log
import dev.palwyn.device.PhoneMonitor
import dev.palwyn.pairing.PairingManager

/**
 * DNS-SD advertisement of <c>_palwyn._tcp</c>, only while it's needed: during pairing, or while paired
 * but not connected. The phone name is included only while pairing, so it isn't broadcast the rest of the time.
 */
object Advertiser {
    private var current: Map<String, String>? = null
    private var listener: NsdManager.RegistrationListener? = null

    @Synchronized
    fun update(c: Context) {
        val app = c.applicationContext
        val port = LinkServer.port
        val id = LinkServer.deviceId
        val pairMode = PairingManager.advertisedMode
        val desired: Map<String, String>? = when {
            port == 0 || id == null -> null
            pairMode != null -> mapOf("id" to id, "pair" to pairMode, "n" to (PhoneMonitor.snapshot.value?.name ?: "Android phone"))
            !LinkServer.hasSessions() && PairedPcs.all(app).isNotEmpty() -> mapOf("id" to id)
            else -> null
        }
        if (desired == current) return
        stop(app)
        if (desired == null) return

        val info = NsdServiceInfo().apply {
            serviceName = "Palwyn"
            serviceType = "_palwyn._tcp"
            this.port = port
            desired.forEach { (k, v) -> setAttribute(k, v) }
        }
        val l = object : NsdManager.RegistrationListener {
            override fun onServiceRegistered(info: NsdServiceInfo) {
                Log.i(LinkService.TAG, "Advertising ${desired.keys}")
            }

            override fun onRegistrationFailed(info: NsdServiceInfo, error: Int) {
                Log.w(LinkService.TAG, "Advertise failed: $error")
            }
            override fun onServiceUnregistered(info: NsdServiceInfo) = Unit
            override fun onUnregistrationFailed(info: NsdServiceInfo, error: Int) = Unit
        }
        app.getSystemService(NsdManager::class.java).registerService(info, NsdManager.PROTOCOL_DNS_SD, l)
        listener = l
        current = desired
    }

    /** Re-announce from scratch, e.g. after joining a Wi-Fi network (the old registration may be gone). */
    @Synchronized
    fun restart(c: Context) {
        stop(c)
        update(c)
    }

    @Synchronized
    fun stop(c: Context) {
        listener?.let {
            try {
                c.applicationContext.getSystemService(NsdManager::class.java).unregisterService(it)
            } catch (e: IllegalArgumentException) { /* already unregistered */ }
        }
        listener = null
        current = null
    }
}
