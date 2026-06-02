package world.homeko.bridge

import android.app.*
import android.content.Context
import android.content.Intent
import android.net.wifi.WifiManager
import android.os.IBinder
import android.os.PowerManager
import android.util.Log
import androidx.core.app.NotificationCompat

private const val TAG = "BridgeService"
private const val CHANNEL_ID = "bridge_channel"
private const val NOTIF_ID   = 1

class BridgeForegroundService : Service() {

    private lateinit var hid: HidKeyboardService
    private lateinit var tcp: TcpServer
    private var wakeLock: PowerManager.WakeLock? = null
    private var wifiLock: WifiManager.WifiLock?  = null
    private var isInitialized = false

    companion object {
        private const val EXTRA_PORT = "port"

        /**
         * Activity bu callback'i set eder. Servis doğrudan buraya yazar — broadcast yok.
         * @Volatile: farklı thread'lerden güvenli okuma için.
         */
        @Volatile
        var statusCallback: ((String) -> Unit)? = null

        /** Son N mesajı sakla; Activity resume'da geçmişi yeniden oynat */
        private val recentMessages = ArrayDeque<String>()
        private const val MAX_RECENT = 50

        fun notifyStatus(msg: String) {
            synchronized(recentMessages) {
                recentMessages.addFirst(msg)
                if (recentMessages.size > MAX_RECENT) recentMessages.removeLast()
            }
            statusCallback?.invoke(msg)
        }

        fun getRecentMessages(): List<String> =
            synchronized(recentMessages) { recentMessages.toList() }

        fun start(context: Context, port: Int) {
            val i = Intent(context, BridgeForegroundService::class.java)
                .putExtra(EXTRA_PORT, port)
            context.startForegroundService(i)
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, BridgeForegroundService::class.java))
        }
    }

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        startForeground(NOTIF_ID, buildNotification("Başlatılıyor…"))

        wakeLock = (getSystemService(POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "HomekoBridge::WakeLock")
            .also { it.acquire(8 * 60 * 60 * 1000L) }

        wifiLock = try {
            (applicationContext.getSystemService(WIFI_SERVICE) as WifiManager)
                .createWifiLock(WifiManager.WIFI_MODE_FULL_LOW_LATENCY, "HomekoBridge::WifiLock")
                .also { it.acquire() }
        } catch (e: Exception) {
            Log.w(TAG, "WifiLock acquire failed (non-fatal): ${e.message}")
            null
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val port = intent?.getIntExtra(EXTRA_PORT, 5556) ?: 5556

        if (!isInitialized) {
            isInitialized = true
            try {
                hid = HidKeyboardService(this)
                hid.onStatusChanged = { msg ->
                    updateNotification(msg)
                    notifyStatus("BLE: $msg")
                }
                hid.register()

                tcp = TcpServer(port, hid) { msg ->
                    notifyStatus(msg)
                }
                tcp.start()

                notifyStatus("TCP $port dinleniyor")
                Log.i(TAG, "Bridge başlatıldı port=$port")
            } catch (e: Exception) {
                Log.e(TAG, "Bridge başlatma HATASI: ${e.javaClass.simpleName}: ${e.message}", e)
                isInitialized = false
                notifyStatus("HATA: ${e.javaClass.simpleName}: ${e.message}")
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        super.onDestroy()
        isInitialized = false
        if (::tcp.isInitialized) tcp.stop()
        if (::hid.isInitialized) hid.unregister()
        wakeLock?.release()
        wifiLock?.release()
    }

    override fun onBind(intent: Intent): IBinder? = null

    private fun updateNotification(text: String) {
        getSystemService(NotificationManager::class.java)
            .notify(NOTIF_ID, buildNotification(text))
    }

    private fun buildNotification(text: String): Notification {
        val pi = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle("HomekoBridge")
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_menu_send)
            .setContentIntent(pi)
            .setOngoing(true)
            .build()
    }

    private fun createNotificationChannel() {
        NotificationChannel(CHANNEL_ID, "HomekoBridge", NotificationManager.IMPORTANCE_LOW)
            .also { getSystemService(NotificationManager::class.java).createNotificationChannel(it) }
    }
}
