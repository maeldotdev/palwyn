package dev.palwyn.link

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.util.Log
import dev.palwyn.Prefs
import dev.palwyn.calls.CallState
import dev.palwyn.calls.Calls
import dev.palwyn.clipboard.ClipboardSync
import dev.palwyn.device.Contacts
import dev.palwyn.device.PhoneMonitor
import dev.palwyn.device.Ringer
import dev.palwyn.drop.Drops
import dev.palwyn.drop.Camera
import dev.palwyn.identity.DeviceIdentity
import dev.palwyn.media.Media
import dev.palwyn.messages.Messages
import dev.palwyn.notifications.Mirror
import dev.palwyn.notifications.PcNotifications
import dev.palwyn.photos.Photos
import dev.palwyn.remote.Remote
import dev.palwyn.screen.ControlService
import dev.palwyn.screen.Screen
import dev.palwyn.identity.Fingerprint
import dev.palwyn.pairing.PairingCrypto
import dev.palwyn.pairing.PairingInvite
import dev.palwyn.pairing.PairingManager
import dev.palwyn.protocol.BadFrameException
import dev.palwyn.protocol.Envelope
import dev.palwyn.protocol.Frames
import dev.palwyn.protocol.Side
import dev.palwyn.protocol.Verdict
import dev.palwyn.protocol.hex
import dev.palwyn.protocol.unhex
import kotlinx.coroutines.CoroutineExceptionHandler
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.filterNotNull
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.withIndex
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.selects.select
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.net.BindException
import java.net.InetSocketAddress
import java.net.Socket
import java.security.KeyStore
import java.security.Principal
import java.security.PrivateKey
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.util.concurrent.ConcurrentHashMap
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLServerSocket
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509ExtendedKeyManager
import javax.net.ssl.X509ExtendedTrustManager
import kotlin.concurrent.thread

/**
 * The phone listens; PCs connect (docs/architecture.md section 3). TLS 1.3, mutual authentication, and a
 * client is accepted only if its certificate is pinned, or it's the PC named in an accepted QR invite, or
 * the user opened a no-camera pairing window.
 */
object LinkServer {
    private const val PREFERRED_PORT = 47800

    /** What this phone can do right now; follows runtime permissions (docs/architecture.md section 5). */
    private fun capabilities(): Set<String> = buildSet {
        add("device")
        add("drop") // files go to our own Download/Palwyn folder: no permission needed
        add("ring") // plays on the alarm stream: no permission needed
        add("remote") // the PC decides what its remote may do (PC_REMOTE); nothing to allow here
        add("screen") // every share starts with the user's tap and Android's own consent dialog
        // Control only while the screen is shared, with Palwyn's switch and the accessibility service both on.
        if (Prefs.screenControl(app) && ControlService.instance != null && Screen.capturing.value) add("screen.control")
        // Opens the phone's own camera app on the user's tap: no camera permission.
        if (app.packageManager.hasSystemFeature(PackageManager.FEATURE_CAMERA_ANY)) add("camera")
        // No permission needed; the user's switch in Settings. Sending is always the user's explicit tap.
        if (Prefs.clipboard(app)) add("clipboard")
        fun granted(p: String) = app.checkSelfPermission(p) == PackageManager.PERMISSION_GRANTED
        if (granted(Manifest.permission.READ_PHONE_STATE)) add("calls.state")
        if (granted(Manifest.permission.ANSWER_PHONE_CALLS)) add("calls.control")
        if (granted(Manifest.permission.READ_CALL_LOG)) add("calls.log")
        if (granted(Manifest.permission.READ_SMS)) add("sms.read")
        if (granted(Manifest.permission.SEND_SMS)) add("sms.send")
        if (Photos.granted(app)) add("photos.read")
        if (granted(Manifest.permission.READ_CONTACTS)) add("contacts.read")
        if (granted(Manifest.permission.WRITE_CONTACTS)) add("contacts.write")
        if (PcNotifications.enabled(app)) add("pc.notifications")
        if (Mirror.granted(app)) {
            add("notifications.read")
            add("notifications.act")
            add("media") // Android shows media sessions only to an enabled notification listener
        }
    }

    /**
     * Call after the user may have granted a permission. Revoking one restarts the app process, so a stale
     * list can only ever be missing capabilities, never claim extra ones.
     */
    fun permissionsChanged() {
        if (!::app.isInitialized) return
        Messages.permissionsChanged()
        Media.start(app)
        val now = capabilities()
        sessions.values.forEach { s ->
            if (s.advertised == now) return@forEach
            val gainedNotifications = "notifications.read" in now && "notifications.read" !in s.advertised
            s.advertised = now
            thread(name = "pb-caps") {
                try {
                    s.conn.send("CAPABILITIES_CHANGED", JSONObject().put("capabilities", JSONArray(now.toList())))
                    if (gainedNotifications) Mirror.current(app).forEach { s.conn.send("NOTIFICATION_POSTED", it) }
                } catch (e: IOException) { /* the session is ending */ }
            }
        }
    }

    private val FREEZING_OEMS = setOf(
        "realme", "oppo", "oneplus", "xiaomi", "redmi", "poco", "vivo", "iqoo", "huawei", "honor", "meizu",
        "tecno", "infinix", "itel",
    )

    /**
     * Heartbeat interval we ask the PC for (seconds). OEMs whose power managers freeze background apps only
     * wake us for incoming traffic, so they need frequent pings (measured on realme UI 4: Phase 0).
     */
    val keepAliveSeconds: Int = if (Build.MANUFACTURER.lowercase() in FREEZING_OEMS) 3 else 15

    @Volatile private var server: SSLServerSocket? = null
    private val sessions = ConcurrentHashMap<String, Session>()
    private lateinit var app: Context
    private lateinit var ownFingerprint: ByteArray

    val port: Int get() = server?.localPort ?: 0
    @Volatile var deviceId: String? = null
        private set

    fun hasSessions() = sessions.isNotEmpty()

    /** Sends to one connected PC (fingerprint hex); returns the message id, or null when it isn't connected. */
    fun send(pc: String, type: String, payload: JSONObject): String? {
        val s = sessions[pc] ?: return null
        val message = Envelope.create(type, payload)
        s.frames.write(message)
        return message.getString("id")
    }
    fun connectedNames(): List<String> = sessions.values.map { it.pcName }

    fun start(c: Context) {
        if (server != null) return
        app = c.applicationContext
        thread(name = "pb-link") {
            try {
                val cert = DeviceIdentity.certificate()
                ownFingerprint = Fingerprint.of(cert.encoded)
                deviceId = Fingerprint.deviceId(ownFingerprint)
                val ss = createServerSocket(cert)
                server = ss
                Log.i(LinkService.TAG, "Listening on port ${ss.localPort}")
                Advertiser.update(app)
                while (!ss.isClosed) {
                    val s = ss.accept() as SSLSocket
                    thread(name = "pb-conn") { handle(s) }
                }
            } catch (e: IOException) {
                Log.i(LinkService.TAG, "Link server stopped: ${e.message}")
            } finally {
                server = null
            }
        }
    }

    fun stop() {
        server?.close()
        sessions.values.forEach { it.close() }
    }

    /** User removed a PC on the phone: tell it (if connected), then forget it. */
    fun unpair(fingerprintHex: String) {
        thread(name = "pb-unpair") {
            sessions[fingerprintHex]?.let { s ->
                try {
                    s.frames.write(Envelope.create("UNPAIR"))
                } catch (e: IOException) { /* it will find out on reconnect */ }
                s.close()
            }
            PairedPcs.remove(app, fingerprintHex)
            Link.refresh(app)
        }
    }

    private fun handle(socket: SSLSocket) {
        try {
            socket.soTimeout = 20_000
            socket.startHandshake()
            val fp = Fingerprint.of(socket.session.peerCertificates[0].encoded)
            val conn = Connection(socket, fp)
            when {
                PairedPcs.contains(app, fp) -> session(conn)
                // The trust manager checked this too, but a resumed TLS session skips it: check again here.
                PairingManager.acceptsForPairing(fp) && PairingManager.claim() -> try {
                    PairingManager.currentInvite?.let { pairQr(conn, it) } ?: pairCode(conn)
                } catch (e: Exception) {
                    PairingManager.fail(app, "The connection to your PC was lost during pairing.")
                    throw e
                }
                else -> Log.i(LinkService.TAG, "Rejected an unexpected connection")
            }
        } catch (e: Exception) {
            Log.i(LinkService.TAG, "Connection ended: ${e.javaClass.simpleName}: ${e.message}")
        } finally {
            try { socket.close() } catch (e: IOException) { }
        }
    }

    // ---- Pairing (phone side) ----

    private fun pairQr(conn: Connection, invite: PairingInvite) {
        conn.send("PAIR_PROOF", JSONObject().put("mac", PairingCrypto.proof(invite.secret, invite.pcFingerprint, ownFingerprint).hex()))
        val result = conn.expect("PAIR_RESULT")
        if (result.getBoolean("ok")) finishPairing(conn.fingerprint, result.getString("name"))
        else PairingManager.fail(app, "Your PC couldn't confirm this phone. Show a new code on the PC and scan it again.")
    }

    private fun pairCode(conn: Connection) = runBlocking {
        run {
            val commit = conn.expect("PAIR_COMMIT").getString("c").unhex()
            val nPhone = ByteArray(16).also(SecureRandom()::nextBytes)
            conn.send("PAIR_NONCE", JSONObject().put("n", nPhone.hex()))
            val nPc = conn.expect("PAIR_REVEAL").getString("n").unhex()
            if (!PairingCrypto.same(PairingCrypto.commit(nPc), commit)) {
                PairingManager.fail(app, "Security check failed. Try pairing again.")
                return@runBlocking
            }
            val mine = PairingManager.showCode(PairingCrypto.code(nPc, nPhone, conn.fingerprint, ownFingerprint))
            val theirs = async(Dispatchers.IO) { conn.expect("PAIR_CONFIRM").getBoolean("accepted") }
            val pcDeclinedFirst = select {
                theirs.onAwait { !it }
                mine.onAwait { false }
            }
            if (pcDeclinedFirst) {
                PairingManager.fail(app, "Pairing was cancelled on the PC.")
                return@runBlocking
            }
            val accepted = mine.await()
            conn.send("PAIR_CONFIRM", JSONObject().put("accepted", accepted))
            if (!accepted) {
                PairingManager.fail(app, "Pairing cancelled.")
                return@runBlocking
            }
            if (!theirs.await()) {
                PairingManager.fail(app, "Pairing was cancelled on the PC.")
                return@runBlocking
            }
            val result = conn.expect("PAIR_RESULT")
            if (result.getBoolean("ok")) finishPairing(conn.fingerprint, result.getString("name"))
            else PairingManager.fail(app, "Pairing failed on the PC.")
        }
    }

    private fun finishPairing(pcFingerprint: ByteArray, pcName: String) {
        PairedPcs.save(app, pcFingerprint, pcName)
        Log.i(LinkService.TAG, "Paired with PC ${Fingerprint.display(Fingerprint.deviceId(pcFingerprint))}")
        PairingManager.succeed(app, pcName)
        Link.refresh(app)
    }

    // ---- Paired session ----

    private fun session(conn: Connection) {
        conn.send("HELLO", hello())
        val first = conn.receive() ?: throw IOException("closed before HELLO")
        // A transfer connection carries one file and ends; it is not a session and replaces none.
        if (first.getString("type") == "TRANSFER_PULL") return transfer(conn, first)
        if (first.getString("type") == "TRANSFER_PUSH") return receiveFile(conn, first)
        if (first.getString("type") == "SCREEN_PULL") {
            // Only the PC the user agreed to share with, and only while the capture runs.
            if (!Screen.stream(app, conn.fingerprint.hex(), conn.socket.outputStream))
                conn.send("ERROR", JSONObject().put("code", "NOT_SHARING"), first.getString("id"))
            return
        }
        if (first.getString("type") != "HELLO") throw BadFrameException("expected HELLO, got ${first.getString("type")}")
        val peer = first.getJSONObject("payload")
        if (peer.getString("deviceId") != Fingerprint.deviceId(conn.fingerprint)) throw BadFrameException("HELLO does not match certificate")
        val name = peer.getString("name")
        PairedPcs.save(app, conn.fingerprint, name)
        // The PC pings every keepAlive seconds; three missed intervals means it's gone (sleep, network loss).
        conn.socket.soTimeout = (3 * keepAliveSeconds + 5) * 1000

        val session = Session(conn, name)
        sessions[conn.fingerprint.hex()]?.close()
        sessions[conn.fingerprint.hex()] = session
        Link.refresh(app)
        Log.i(LinkService.TAG, "Session started")
        try {
            PhoneMonitor.snapshot.value?.let { p ->
                conn.send("DEVICE_INFO", JSONObject()
                    .put("manufacturer", p.manufacturer).put("model", p.model).put("androidVersion", p.androidVersion)
                    .put("sdk", p.sdk).put("storageTotal", p.storageTotalBytes).put("storageFree", p.storageFreeBytes))
                p.batteryPercent?.let { conn.send("BATTERY_CHANGED", battery(it, p.charging)) }
            }
            session.scope.launch {
                PhoneMonitor.snapshot.filterNotNull()
                    .map { it.batteryPercent to it.charging }
                    .distinctUntilChanged().drop(1)
                    .collect { (level, charging) -> if (level != null) conn.send("BATTERY_CHANGED", battery(level, charging)) }
            }
            session.scope.launch {
                // The first value is the call in progress when the PC connected, or an old one that already ended.
                Calls.current.filterNotNull().withIndex().collect { (i, call) ->
                    if (i == 0 && call.state == CallState.ENDED) return@collect
                    if ("calls.state" in session.advertised) conn.send("CALL_STATE", call.toPayload())
                }
            }
            session.scope.launch {
                if ("notifications.read" in session.advertised) Mirror.current(app).forEach { conn.send("NOTIFICATION_POSTED", it) }
                Mirror.events.collect { (type, p) -> if ("notifications.read" in session.advertised) conn.send(type, p) }
            }
            session.scope.launch {
                ClipboardSync.outgoing.collect { conn.send("CLIPBOARD_SET", JSONObject().put("text", it)) }
            }
            session.scope.launch {
                Drops.outgoing.collect { conn.send("DROP_OFFER", it) }
            }
            session.scope.launch {
                PhoneMonitor.snapshot.filterNotNull()
                    .map { DeviceStatus(it.onWifi, it.wifiSignal, it.bluetooth, it.storageFreeBytes, it.cellSignal, it.cellNetwork, it.carrier) }
                    .distinctUntilChanged()
                    .collect { conn.send("DEVICE_STATUS", it.toPayload()) }
            }
            session.scope.launch {
                Media.now.collect { now ->
                    if ("media" !in session.advertised) return@collect
                    conn.send("MEDIA_STATE", now?.let {
                        JSONObject().put("active", true).put("app", it.app).put("title", it.title).put("artist", it.artist).put("playing", it.playing)
                    } ?: JSONObject().put("active", false))
                }
            }
            session.scope.launch {
                Messages.added.collect { if ("sms.read" in session.advertised) conn.send("SMS_RECEIVED", it) }
            }
            while (true) {
                val m = conn.receive() ?: break
                val id = m.getString("id")
                val payload = m.getJSONObject("payload")
                when (val type = m.getString("type")) {
                    "PING" -> conn.send("PONG", replyTo = id)
                    "UNPAIR" -> {
                        Log.i(LinkService.TAG, "PC removed this phone")
                        PairedPcs.remove(app, conn.fingerprint.hex())
                        break
                    }
                    "CALL_ANSWER", "CALL_DECLINE", "CALL_END" -> {
                        val error = Calls.command(type, payload.getString("callId"))
                        Log.i(LinkService.TAG, "$type from PC: ${error ?: "ok"}")
                        if (error == null) conn.send("CALL_RESULT", JSONObject().put("ok", true), id)
                        else conn.send("ERROR", JSONObject().put("code", error), id)
                    }
                    "SMS_THREADS_GET" -> session.scope.launch {
                        reply(conn, id, "SMS_THREADS") {
                            JSONObject().put("threads", Messages.threads(payload.getInt("limit"), payload.optLongOrNull("before"), payload.optQuery()))
                        }
                    }
                    "SMS_MESSAGES_GET" -> session.scope.launch {
                        reply(conn, id, "SMS_MESSAGES") {
                            JSONObject().put("messages", Messages.messages(
                                payload.getString("threadId"), payload.getInt("limit"), payload.optLongOrNull("before")))
                        }
                    }
                    "SMS_SEND" -> session.scope.launch {
                        val error = Messages.send(payload.getString("address"), payload.getString("body"))
                        if (error == null) conn.send("SMS_SEND_RESULT", JSONObject().put("ok", true), id)
                        else conn.send("ERROR", JSONObject().put("code", error), id)
                    }
                    "MMS_SEND" -> session.scope.launch {
                        val to = payload.getJSONArray("addresses").let { a -> (0 until a.length()).map(a::getString) }
                        val error = Messages.sendGroup(to, payload.getString("body"))
                        if (error == null) conn.send("SMS_SEND_RESULT", JSONObject().put("ok", true), id)
                        else conn.send("ERROR", JSONObject().put("code", error), id)
                    }
                    "NOTIFICATION_DISMISS", "NOTIFICATION_ACTION" -> {
                        val key = payload.getString("key")
                        val error = if (type == "NOTIFICATION_DISMISS") Mirror.dismiss(key)
                        else Mirror.act(app, key, payload.getInt("index"), payload.optString("replyText").ifEmpty { null })
                        Log.i(LinkService.TAG, "$type from PC: ${error ?: "ok"}")
                        if (error == null) conn.send("NOTIFICATION_RESULT", JSONObject().put("ok", true), id)
                        else conn.send("ERROR", JSONObject().put("code", error), id)
                    }
                    "APP_ICON_GET" -> session.scope.launch {
                        val pkg = payload.getString("package")
                        val png = Mirror.icon(app, pkg)
                        if (png != null && png.length <= 90_000) conn.send("APP_ICON", JSONObject().put("package", pkg).put("png", png), id)
                        else conn.send("ERROR", JSONObject().put("code", "FAILED"), id)
                    }
                    "DROP_TEXT" -> {
                        Drops.text(app, payload.getString("text"))
                        conn.send("DROP_DONE", JSONObject().put("ok", true), id)
                    }
                    "CLIPBOARD_SET" -> ClipboardSync.received(app, payload.getString("text"))
                    "PC_NOTIFICATION" -> PcNotifications.show(app, payload)
                    "PC_NOTIFICATION_REMOVED" -> PcNotifications.remove(app, payload.getString("id"))
                    "PC_REMOTE" -> Remote.update(conn.fingerprint.hex(), name, payload)
                    "SCREEN_REQUEST" -> {
                        Screen.request(app, conn.fingerprint.hex())
                        conn.send("SCREEN_RESULT", JSONObject().put("ok", true), id)
                    }
                    "SCREEN_TOUCH", "SCREEN_KEY", "SCREEN_TEXT" -> {
                        // Re-checked here: the capability says control is on, not that this PC is the one watching.
                        val control = ControlService.instance
                        if (control != null && Screen.capturing.value && Screen.requester == conn.fingerprint.hex()) when (type) {
                            "SCREEN_TOUCH" -> control.touch(payload.getInt("x1"), payload.getInt("y1"), payload.getInt("x2"), payload.getInt("y2"), payload.getInt("ms"))
                            "SCREEN_KEY" -> control.key(payload.getString("key"))
                            else -> control.type(payload.getString("text"))
                        }
                    }
                    "REMOTE_RESULT", "ERROR" ->
                        m.optString("replyTo").takeIf { it.isNotEmpty() }?.let { Remote.replied(app, it, type, payload) }
                    "DROP_RESULT" -> Drops.finished(payload.getString("dropId"), payload.getBoolean("ok"))
                    "PHOTOS_GET" -> session.scope.launch {
                        reply(conn, id, "PHOTOS") {
                            JSONObject().put("photos", Photos.list(app, payload.getInt("limit"), payload.optString("beforeId").toLongOrNull(),
                                payload.optString("album").ifEmpty { null }))
                        }
                    }
                    "PHOTO_THUMB_GET" -> session.scope.launch {
                        val photoId = payload.getString("id")
                        reply(conn, id, "PHOTO_THUMB") {
                            val jpeg = Photos.thumbnail(app, photoId.toLongOrNull() ?: throw IllegalArgumentException("bad id"), payload.optBoolean("video"))
                            JSONObject().put("id", photoId).put("jpeg", jpeg)
                        }
                    }
                    "ALBUMS_GET" -> session.scope.launch {
                        reply(conn, id, "ALBUMS") { JSONObject().put("albums", Photos.albums(app)) }
                    }
                    "CAMERA_REQUEST" -> {
                        Camera.request(app)
                        conn.send("CAMERA_RESULT", JSONObject().put("ok", true), id)
                    }
                    "RING" -> {
                        Ringer.ring(app, payload.getBoolean("on"))
                        conn.send("RING_RESULT", JSONObject().put("ok", true), id)
                    }
                    "MEDIA_CONTROL" -> {
                        val error = Media.control(app, payload.getString("action"))
                        if (error == null) conn.send("MEDIA_RESULT", JSONObject().put("ok", true), id)
                        else conn.send("ERROR", JSONObject().put("code", error), id)
                    }
                    "CALL_LOG_GET" -> session.scope.launch {
                        reply(conn, id, "CALL_LOG") {
                            JSONObject().put("calls", Calls.history(payload.getInt("limit"), payload.optLongOrNull("before"), payload.optQuery()))
                        }
                    }
                    "CONTACTS_GET" -> session.scope.launch {
                        reply(conn, id, "CONTACTS") { Contacts.page(app, payload.optInt("offset", 0), payload.getInt("limit")) }
                    }
                    "CONTACT_PHOTO_GET" -> session.scope.launch {
                        val contactId = payload.getString("id")
                        reply(conn, id, "CONTACT_PHOTO") {
                            val jpeg = Contacts.photo(app, contactId.toLongOrNull() ?: throw IllegalArgumentException("bad id"))
                            JSONObject().put("id", contactId).apply { if (jpeg != null && jpeg.length <= 200_000) put("jpeg", jpeg) }
                        }
                    }
                    "CONTACT_SAVE" -> session.scope.launch {
                        reply(conn, id, "CONTACT_RESULT") {
                            val saved = Contacts.save(app, payload.optString("id").toLongOrNull(), payload)
                            JSONObject().put("ok", true).put("id", "$saved")
                        }
                    }
                    "CONTACT_DELETE" -> session.scope.launch {
                        reply(conn, id, "CONTACT_RESULT") {
                            JSONObject().put("ok", payload.getString("id").toLongOrNull()?.let { Contacts.delete(app, it) } == true)
                        }
                    }
                }
            }
        } finally {
            session.close()
            PcNotifications.clear(app)
            Remote.remove(conn.fingerprint.hex())
            sessions.remove(conn.fingerprint.hex(), session)
            Link.refresh(app)
            Log.i(LinkService.TAG, "Session ended")
        }
    }

    /**
     * Sends one photo on its own connection (docs/architecture.md section 3), so a large file never delays
     * call or message events on the session: TRANSFER_START, then exactly `size` raw bytes, then close.
     */
    private fun transfer(conn: Connection, m: JSONObject) {
        val id = m.getString("id")
        val p = m.getJSONObject("payload")
        val kind = p.getString("kind")
        var offer: Drops.Offer? = null
        val original = try {
            when (kind) {
                // Photos need the permission; a drop item is allowed only while the phone is offering it.
                "photo", "video" -> if ("photos.read" !in capabilities()) null
                    else p.getString("id").toLongOrNull()?.let { Photos.open(app, it, video = kind == "video") }
                "mms" -> if ("sms.read" !in capabilities()) null else p.getString("id").toLongOrNull()?.let(Messages::part)
                else -> Drops.find(p.getString("id"))?.let { (o, item) ->
                    offer = o
                    Photos.open(app, item.uri, item.name)?.let { Photos.Original(item.name, item.mime, it.size, it.stream) }
                }
            }
        } catch (e: SecurityException) {
            null
        } catch (e: java.io.FileNotFoundException) {
            null
        }
        if (original == null) {
            conn.send("ERROR", JSONObject().put("code", "FAILED"), id)
            return
        }
        val started = System.nanoTime()
        original.stream.use { input ->
            conn.send("TRANSFER_START", JSONObject().put("size", original.size).put("name", original.name).put("mime", original.mime), id)
            val out = conn.socket.outputStream
            val buffer = ByteArray(64 * 1024)
            var left = original.size
            while (left > 0) {
                val n = input.read(buffer, 0, minOf(buffer.size.toLong(), left).toInt())
                if (n < 0) break // shorter than announced: the PC sees a short read and fails the copy
                out.write(buffer, 0, n)
                left -= n
                offer?.let { Drops.sent(it, n.toLong()) }
            }
            out.flush()
        }
        Log.i(LinkService.TAG, "Sent $kind: ${original.size / 1024} KB in ${(System.nanoTime() - started) / 1_000_000} ms")
    }

    /** TRANSFER_PUSH: a file from the PC, then exactly `size` raw bytes; answered TRANSFER_DONE or ERROR. */
    private fun receiveFile(conn: Connection, m: JSONObject) {
        val p = m.getJSONObject("payload")
        val started = System.nanoTime()
        val size = p.getLong("size")
        try {
            if (p.getString("kind") == "clipboard") {
                if ("clipboard" !in capabilities()) throw SecurityException("clipboard sharing is off")
                ClipboardSync.receiveImage(app, p.getString("mime"), size, conn.frames::readRaw)
            } else Drops.receiveFile(app, p.getString("name"), p.getString("mime"), size, conn.frames::readRaw, p.optString("folder").ifEmpty { null })
        } catch (e: Exception) {
            Log.w(LinkService.TAG, "Receiving a file failed: ${e.javaClass.simpleName}")
            runCatching { conn.send("ERROR", JSONObject().put("code", "FAILED"), m.getString("id")) }
            return
        }
        PhoneMonitor.refreshStorage()
        conn.send("TRANSFER_DONE", JSONObject().put("ok", true), m.getString("id"))
        Log.i(LinkService.TAG, "Received file: ${size / 1024} KB in ${(System.nanoTime() - started) / 1_000_000} ms")
    }

    /** Answers a read request; a provider refusing us (permission revoked, OEM quirk) becomes an ERROR, not a dropped link. */
    private fun reply(conn: Connection, id: String, type: String, build: () -> JSONObject) {
        val payload = try {
            build()
        } catch (e: SecurityException) {
            conn.send("ERROR", JSONObject().put("code", "PERMISSION_DENIED"), id)
            return
        } catch (e: Exception) { // includes IOException: a photo deleted since it was listed
            Log.w(LinkService.TAG, "$type failed: ${e.javaClass.simpleName}")
            conn.send("ERROR", JSONObject().put("code", "FAILED"), id)
            return
        }
        conn.send(type, payload, id)
    }

    private fun JSONObject.optLongOrNull(key: String) = if (has(key)) getLong(key) else null
    private fun JSONObject.optQuery() = optString("query").trim().takeIf { it.isNotEmpty() }

    private fun battery(level: Int, charging: Boolean) = JSONObject().put("level", level).put("charging", charging)

    private data class DeviceStatus(
        val wifi: Boolean, val wifiSignal: Int?, val bluetooth: Boolean?, val storageFree: Long,
        val cellSignal: Int?, val cellNetwork: String?, val carrier: String?,
    ) {
        fun toPayload(): JSONObject = JSONObject().put("wifi", wifi).put("wifiSignal", wifiSignal)
            .put("bluetooth", bluetooth).put("storageFree", storageFree)
            .put("cellSignal", cellSignal).put("cellNetwork", cellNetwork).put("carrier", carrier)
    }

    private fun hello() = JSONObject()
        .put("protocol", JSONObject().put("min", Envelope.VERSION).put("max", Envelope.VERSION))
        .put("app", app.packageManager.getPackageInfo(app.packageName, 0).versionName ?: "0")
        .put("deviceId", deviceId)
        .put("name", PhoneMonitor.snapshot.value?.name ?: "Android phone")
        .put("platform", "android")
        .put("capabilities", JSONArray(capabilities().toList()))
        .put("keepAlive", keepAliveSeconds)

    private class Session(val conn: Connection, val pcName: String) {
        /** What the PC was last told this phone can do. */
        @Volatile var advertised = capabilities()
        // A send racing the socket close throws; that ends the session anyway, so it must not crash the app.
        val scope = CoroutineScope(
            Dispatchers.IO + SupervisorJob() + CoroutineExceptionHandler { _, e ->
                Log.i(LinkService.TAG, "Session task ended: ${e.javaClass.simpleName}: ${e.message}")
            }
        )
        val frames get() = conn.frames
        fun close() {
            scope.cancel()
            try { conn.socket.close() } catch (e: IOException) { }
        }
    }

    private class Connection(val socket: SSLSocket, val fingerprint: ByteArray) {
        val frames = Frames(socket.inputStream, socket.outputStream)

        fun send(type: String, payload: JSONObject = JSONObject(), replyTo: String? = null) =
            frames.write(Envelope.create(type, payload, replyTo))

        fun receive(): JSONObject? {
            while (true) {
                val body = frames.read() ?: return null
                val (verdict, message) = Envelope.validate(body, Side.Phone, capabilities())
                when (verdict) {
                    Verdict.Accept -> return message
                    is Verdict.Error -> send("ERROR", JSONObject().put("code", verdict.code), verdict.id)
                    Verdict.Close -> throw BadFrameException("invalid frame")
                }
            }
        }

        fun expect(type: String): JSONObject {
            val m = receive() ?: throw IOException("closed while waiting for $type")
            val actual = m.getString("type")
            if (actual != type) throw BadFrameException("expected $type, got $actual")
            return m.getJSONObject("payload")
        }
    }

    // ---- TLS ----

    private fun createServerSocket(cert: X509Certificate): SSLServerSocket {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val key = ks.getKey(DeviceIdentity.ALIAS, null) as PrivateKey
        val context = SSLContext.getInstance("TLSv1.3").apply {
            init(arrayOf(IdentityKeyManager(key, cert)), arrayOf(PinningTrustManager()), null)
        }
        val ss = context.serverSocketFactory.createServerSocket() as SSLServerSocket
        ss.reuseAddress = true
        try {
            ss.bind(InetSocketAddress(PREFERRED_PORT))
        } catch (e: BindException) {
            ss.bind(InetSocketAddress(0))
        }
        ss.needClientAuth = true
        ss.enabledProtocols = arrayOf("TLSv1.3")
        return ss
    }

    private class IdentityKeyManager(val key: PrivateKey, val cert: X509Certificate) : X509ExtendedKeyManager() {
        override fun chooseServerAlias(keyType: String?, issuers: Array<out Principal>?, socket: Socket?) = DeviceIdentity.ALIAS
        override fun chooseEngineServerAlias(keyType: String?, issuers: Array<out Principal>?, engine: SSLEngine?) = DeviceIdentity.ALIAS
        override fun getCertificateChain(alias: String?) = arrayOf(cert)
        override fun getPrivateKey(alias: String?) = key
        override fun getServerAliases(keyType: String?, issuers: Array<out Principal>?) = arrayOf(DeviceIdentity.ALIAS)
        override fun getClientAliases(keyType: String?, issuers: Array<out Principal>?) = null
        override fun chooseClientAlias(keyType: Array<out String>?, issuers: Array<out Principal>?, socket: Socket?) = null
    }

    /** Trust is the fingerprint, not a CA: no chain building, no hostname checks. */
    @SuppressLint("CustomX509TrustManager")
    private class PinningTrustManager : X509ExtendedTrustManager() {
        private fun check(chain: Array<out X509Certificate>?) {
            val fp = Fingerprint.of(chain?.firstOrNull()?.encoded ?: throw CertificateException("no certificate"))
            if (!PairedPcs.contains(app, fp) && !PairingManager.acceptsForPairing(fp)) throw CertificateException("unknown PC")
        }

        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) = check(chain)
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) = check(chain)
        override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) = check(chain)
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) = throw CertificateException("server role only")
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, socket: Socket?) = throw CertificateException("server role only")
        override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?, engine: SSLEngine?) = throw CertificateException("server role only")
        override fun getAcceptedIssuers() = emptyArray<X509Certificate>()
    }
}
