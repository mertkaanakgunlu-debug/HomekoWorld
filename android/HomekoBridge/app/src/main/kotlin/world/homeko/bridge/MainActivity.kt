package world.homeko.bridge

import android.Manifest
import android.bluetooth.*
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.PowerManager
import android.provider.Settings
import android.widget.*
import java.net.NetworkInterface
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat

class MainActivity : AppCompatActivity() {

    private lateinit var tvWifiIp:    TextView
    private lateinit var tvUsbIp:    TextView
    private lateinit var tvBtStatus: TextView
    private lateinit var tvLog:      TextView
    private lateinit var btnStart:   Button
    private lateinit var btnStop:    Button
    private lateinit var btnRefreshIp: Button
    private lateinit var etPort:     EditText

    private val logs = ArrayDeque<String>(50)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        tvWifiIp     = findViewById(R.id.tvWifiIp)
        tvUsbIp      = findViewById(R.id.tvUsbIp)
        tvBtStatus   = findViewById(R.id.tvBtStatus)
        tvLog        = findViewById(R.id.tvLog)
        btnStart     = findViewById(R.id.btnStart)
        btnStop      = findViewById(R.id.btnStop)
        btnRefreshIp = findViewById(R.id.btnRefreshIp)
        etPort       = findViewById(R.id.etPort)

        updateIpDisplay()
        requestBatteryOptimizationExemption()

        btnRefreshIp.setOnClickListener { updateIpDisplay() }

        btnStart.setOnClickListener {
            val port = etPort.text.toString().toIntOrNull() ?: 5556
            requestPermissionsAndStart(port)
        }

        btnStop.setOnClickListener { stopBridge() }
    }

    override fun onResume() {
        super.onResume()
        updateIpDisplay()
        BridgeForegroundService.statusCallback = { msg ->
            runOnUiThread {
                if (msg.startsWith("BLE: ")) tvBtStatus.text = msg.removePrefix("BLE: ")
                appendLog(msg)
            }
        }
        BridgeForegroundService.getRecentMessages().reversed().forEach { msg ->
            if (msg.startsWith("BLE: ")) tvBtStatus.text = msg.removePrefix("BLE: ")
            appendLog(msg)
        }
    }

    override fun onPause() {
        super.onPause()
        BridgeForegroundService.statusCallback = null
    }

    private fun requestPermissionsAndStart(port: Int) {
        val needed = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED)
                needed.add(Manifest.permission.BLUETOOTH_CONNECT)
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_ADVERTISE) != PackageManager.PERMISSION_GRANTED)
                needed.add(Manifest.permission.BLUETOOTH_ADVERTISE)
        }
        if (needed.isNotEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 100)
            return
        }
        startBridge(port)
    }

    override fun onRequestPermissionsResult(code: Int, perms: Array<String>, results: IntArray) {
        super.onRequestPermissionsResult(code, perms, results)
        if (code == 100 && results.all { it == PackageManager.PERMISSION_GRANTED }) {
            startBridge(etPort.text.toString().toIntOrNull() ?: 5556)
        } else {
            appendLog("BT izinleri reddedildi!")
        }
    }

    private fun startBridge(port: Int) {
        appendLog("Bridge başlatılıyor (port $port)...")
        btnStart.isEnabled = false
        btnStart.text = "Çalışıyor"
        btnStop.isEnabled = true
        BridgeForegroundService.start(this, port)
    }

    private fun stopBridge() {
        BridgeForegroundService.stop(this)
        btnStart.isEnabled = true
        btnStart.text = "Başlat"
        btnStop.isEnabled = false
        tvBtStatus.text = "Durduruldu"
        appendLog("Bridge durduruldu.")
    }

    private fun requestBatteryOptimizationExemption() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            val pm = getSystemService(POWER_SERVICE) as PowerManager
            if (!pm.isIgnoringBatteryOptimizations(packageName)) {
                try {
                    startActivity(
                        Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS)
                            .setData(Uri.parse("package:$packageName"))
                    )
                } catch (_: Exception) {}
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
    }

    private fun appendLog(msg: String) {
        logs.addFirst(msg)
        if (logs.size > 50) logs.removeLast()
        tvLog.text = logs.joinToString("\n")
    }

    private fun updateIpDisplay() {
        val (wifiIp, usbIp) = getNetworkAddresses()
        tvWifiIp.text = if (wifiIp != null) "WiFi:  $wifiIp" else "WiFi:  bağlı değil"
        tvUsbIp.text  = if (usbIp  != null) "USB:   $usbIp  ← PC'de bunu kullan"
                        else                 "USB:   kablo/tethering bağlı değil"
    }

    /**
     * Returns (wifiIp, usbIp). USB tethering creates an rndis0/usb0 interface
     * with a fixed IP (typically 192.168.42.129 on stock Android).
     */
    private fun getNetworkAddresses(): Pair<String?, String?> {
        var wifiIp: String? = null
        var usbIp:  String? = null
        try {
            NetworkInterface.getNetworkInterfaces()?.asSequence()?.forEach { iface ->
                val ip = iface.inetAddresses.asSequence()
                    .firstOrNull { addr ->
                        !addr.isLoopbackAddress &&
                        !addr.isLinkLocalAddress &&
                        addr.hostAddress?.contains(':') == false
                    }
                    ?.hostAddress ?: return@forEach

                val name = iface.name.lowercase()
                when {
                    name.startsWith("wlan") || name.startsWith("eth") ->
                        if (wifiIp == null) wifiIp = ip
                    name.startsWith("rndis") || name == "usb0" ->
                        usbIp = ip
                }
            }
        } catch (_: Exception) {}
        return Pair(wifiIp, usbIp)
    }
}
