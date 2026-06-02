package world.homeko.bridge

import android.annotation.SuppressLint
import android.bluetooth.*
import android.bluetooth.le.*
import android.content.Context
import android.os.Build
import android.os.ParcelUuid
import android.util.Log
import java.util.UUID

private const val TAG = "BleHidGattServer"

// ── Service UUIDs ─────────────────────────────────────────────────────────────
private val UUID_HID_SERVICE     = UUID.fromString("00001812-0000-1000-8000-00805f9b34fb")
private val UUID_BATTERY_SERVICE = UUID.fromString("0000180f-0000-1000-8000-00805f9b34fb")
private val UUID_DEVINFO_SERVICE = UUID.fromString("0000180a-0000-1000-8000-00805f9b34fb")

// ── Characteristic UUIDs ──────────────────────────────────────────────────────
private val UUID_HID_INFO      = UUID.fromString("00002a4a-0000-1000-8000-00805f9b34fb")
private val UUID_REPORT_MAP    = UUID.fromString("00002a4b-0000-1000-8000-00805f9b34fb")
private val UUID_HID_CONTROL   = UUID.fromString("00002a4c-0000-1000-8000-00805f9b34fb")
private val UUID_REPORT        = UUID.fromString("00002a4d-0000-1000-8000-00805f9b34fb")
private val UUID_PROTOCOL_MODE = UUID.fromString("00002a4e-0000-1000-8000-00805f9b34fb")
private val UUID_BATTERY_LEVEL = UUID.fromString("00002a19-0000-1000-8000-00805f9b34fb")
private val UUID_PNP_ID        = UUID.fromString("00002a50-0000-1000-8000-00805f9b34fb")

// ── Descriptor UUIDs ──────────────────────────────────────────────────────────
private val UUID_CCCD       = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb")
private val UUID_REPORT_REF = UUID.fromString("00002908-0000-1000-8000-00805f9b34fb")

// HID Info value: bcdHID=1.11 (little-endian 0x0111), countryCode=0, flags=NormallyConnectable
private val HID_INFO = byteArrayOf(0x11, 0x01, 0x00, 0x02)

// PnP ID: source=USB(0x02), VID=0x045E (Microsoft), PID=0x0750, version=0x0100
// Using MS VID ensures Windows selects the inbox keyboard driver without prompting.
private val PNP_ID = byteArrayOf(0x02, 0x5E.toByte(), 0x04, 0x50, 0x07, 0x00, 0x01)

/**
 * BLE HID peripheral using HID over GATT Profile (HOGP / Bluetooth SIG HID Service 0x1812).
 *
 * Windows connects, pairs (Just Works), reads the Report Map, enables CCCD notifications,
 * then installs the in-box HID keyboard driver.  From that point on every [sendReport] call
 * becomes a GATT notification that the OS delivers to the kernel HID stack — jitter ±2–3 ms
 * vs ±10–25 ms for Classic BT HID.
 *
 * Thread safety: [connectedDevice] and [notificationsEnabled] are @Volatile; [sendReport]
 * is safe to call from any thread.
 */
@SuppressLint("MissingPermission")
class BleHidGattServer(
    private val context: Context,
    private val onStatus: (String) -> Unit,
) {
    private val btManager = context.getSystemService(Context.BLUETOOTH_SERVICE) as BluetoothManager
    private val adapter   = btManager.adapter

    private var gattServer: BluetoothGattServer? = null
    private var advertiser: BluetoothLeAdvertiser? = null

    @Volatile private var connectedDevice: BluetoothDevice? = null
    @Volatile private var notificationsEnabled = false
    @Volatile private var isAdvertising = false
    private var originalBtName: String? = null

    // Services are added sequentially via onServiceAdded to avoid Android async race.
    private val pendingServices = ArrayDeque<BluetoothGattService>()

    // Held here so sendReport() can reference it without a service/char lookup each call.
    private lateinit var reportChar: BluetoothGattCharacteristic

    /** True only when a device is connected AND has enabled CCCD notifications. */
    val isConnected: Boolean get() = connectedDevice != null && notificationsEnabled

    // ── GATT server callback ──────────────────────────────────────────────────

    private val gattCallback = object : BluetoothGattServerCallback() {

        // addService() is asynchronous — only add the next service after this fires.
        // Starting advertising before all services are registered causes Windows to
        // read incomplete GATT tables, fail HID driver init, and disconnect.
        override fun onServiceAdded(status: Int, service: BluetoothGattService) {
            if (pendingServices.isNotEmpty()) {
                gattServer?.addService(pendingServices.removeFirst())
            } else {
                // All services registered — safe to advertise now.
                startAdvertising()
            }
        }

        override fun onConnectionStateChange(device: BluetoothDevice, status: Int, newState: Int) {
            val name = device.name ?: device.address
            when (newState) {
                BluetoothProfile.STATE_CONNECTED -> {
                    Log.i(TAG, "BLE connected: $name (status=$status)")
                    connectedDevice = device
                    notificationsEnabled = false
                    onStatus("BLE HID bağlı: $name")
                    stopAdvertising()
                }
                BluetoothProfile.STATE_DISCONNECTED -> {
                    Log.i(TAG, "BLE disconnected: $name (status=$status)")
                    if (connectedDevice?.address == device.address) {
                        connectedDevice = null
                        notificationsEnabled = false
                    }
                    onStatus("BLE HID kesildi — yeniden reklam başlatılıyor")
                    startAdvertising()
                }
            }
        }

        override fun onCharacteristicReadRequest(
            device: BluetoothDevice, requestId: Int, offset: Int,
            characteristic: BluetoothGattCharacteristic,
        ) {
            val full = when (characteristic.uuid) {
                UUID_HID_INFO      -> HID_INFO
                UUID_REPORT_MAP    -> HidDescriptor.KEYBOARD
                UUID_PROTOCOL_MODE -> byteArrayOf(0x01) // Report Protocol Mode
                UUID_REPORT        -> HidDescriptor.EMPTY_REPORT
                UUID_BATTERY_LEVEL -> byteArrayOf(100)
                UUID_PNP_ID        -> PNP_ID
                else               -> byteArrayOf()
            }
            val slice = if (offset < full.size) full.copyOfRange(offset, full.size) else byteArrayOf()
            gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, slice)
        }

        override fun onCharacteristicWriteRequest(
            device: BluetoothDevice, requestId: Int,
            characteristic: BluetoothGattCharacteristic,
            preparedWrite: Boolean, responseNeeded: Boolean,
            offset: Int, value: ByteArray,
        ) {
            // HID Control Point (suspend/exit-suspend) — just acknowledge.
            if (responseNeeded) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
            }
        }

        override fun onDescriptorReadRequest(
            device: BluetoothDevice, requestId: Int, offset: Int,
            descriptor: BluetoothGattDescriptor,
        ) {
            val value = when (descriptor.uuid) {
                UUID_CCCD ->
                    if (notificationsEnabled) BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE
                    else                      BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE
                UUID_REPORT_REF -> byteArrayOf(0x00, 0x01) // reportId=0, type=Input
                else            -> byteArrayOf()
            }
            gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, offset, value)
        }

        override fun onDescriptorWriteRequest(
            device: BluetoothDevice, requestId: Int,
            descriptor: BluetoothGattDescriptor,
            preparedWrite: Boolean, responseNeeded: Boolean,
            offset: Int, value: ByteArray,
        ) {
            if (descriptor.uuid == UUID_CCCD) {
                val enabled = value.contentEquals(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE)
                notificationsEnabled = enabled
                descriptor.value = value
                Log.i(TAG, "CCCD: notifications ${if (enabled) "ENABLED" else "DISABLED"}")
                if (enabled) onStatus("BLE HID hazır — tuş gönderimi aktif")
            }
            if (responseNeeded) {
                gattServer?.sendResponse(device, requestId, BluetoothGatt.GATT_SUCCESS, 0, null)
            }
        }
    }

    // ── Advertise callback ────────────────────────────────────────────────────

    private val advertiseCallback = object : AdvertiseCallback() {
        override fun onStartSuccess(settingsInEffect: AdvertiseSettings) {
            isAdvertising = true
            Log.i(TAG, "BLE advertising started")
            onStatus("BLE HID reklam başladı — PC'den bağlanmasını bekle")
        }
        override fun onStartFailure(errorCode: Int) {
            // ALREADY_STARTED means advertising is already running — not a real error.
            if (errorCode == ADVERTISE_FAILED_ALREADY_STARTED) {
                isAdvertising = true
                Log.d(TAG, "startAdvertising: already running, ignored")
                return
            }
            isAdvertising = false
            val reason = when (errorCode) {
                ADVERTISE_FAILED_DATA_TOO_LARGE      -> "DATA_TOO_LARGE"
                ADVERTISE_FAILED_FEATURE_UNSUPPORTED -> "FEATURE_UNSUPPORTED"
                ADVERTISE_FAILED_INTERNAL_ERROR      -> "INTERNAL_ERROR"
                ADVERTISE_FAILED_TOO_MANY_ADVERTISERS -> "TOO_MANY_ADVERTISERS"
                else                                  -> "ERROR_$errorCode"
            }
            Log.e(TAG, "BLE advertising FAILED: $reason")
            onStatus("⚠ BLE reklam başlatılamadı: $reason")
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    fun start() {
        if (!adapter.isEnabled) {
            onStatus("⚠ Bluetooth kapalı — lütfen açın")
            return
        }
        if (!adapter.isMultipleAdvertisementSupported) {
            onStatus("⚠ Bu cihaz BLE advertisement desteklemiyor")
            return
        }
        // Use a short fixed name so the device is identifiable in Windows "Add device"
        // and scan response stays well under the 31-byte limit.
        originalBtName = adapter.name
        adapter.name = "Homeko"
        openGattServer()
        // startAdvertising() is called from onServiceAdded after all services are ready
    }

    fun stop() {
        stopAdvertising()
        gattServer?.close()
        gattServer = null
        connectedDevice = null
        notificationsEnabled = false
        // Restore original Bluetooth name
        originalBtName?.let { adapter.name = it }
        originalBtName = null
    }

    /** Send an 8-byte HID keyboard report.  No-op if no device is connected or CCCD not enabled. */
    fun sendReport(report: ByteArray) {
        val device = connectedDevice ?: return
        if (!notificationsEnabled) return
        val server = gattServer ?: return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            server.notifyCharacteristicChanged(device, reportChar, false, report)
        } else {
            @Suppress("DEPRECATION")
            reportChar.value = report
            @Suppress("DEPRECATION")
            server.notifyCharacteristicChanged(device, reportChar, false)
        }
    }

    // ── Private: GATT server setup ────────────────────────────────────────────

    private fun openGattServer() {
        gattServer = btManager.openGattServer(context, gattCallback)
        if (gattServer == null) {
            onStatus("⚠ GATT sunucu açılamadı")
            return
        }
        // Queue Battery and DevInfo; HID is added first and triggers the chain via
        // onServiceAdded. Advertising starts only after the last service is registered.
        pendingServices.clear()
        pendingServices.add(buildBatteryService())
        pendingServices.add(buildDevInfoService())
        gattServer!!.addService(buildHidService())
        Log.i(TAG, "GATT server opened — adding services sequentially")
    }

    private fun buildHidService(): BluetoothGattService {
        val service = BluetoothGattService(UUID_HID_SERVICE,
            BluetoothGattService.SERVICE_TYPE_PRIMARY)

        // No ENCRYPTED permissions anywhere — Windows HID driver must be able to read
        // the Report Map and write CCCD without requiring prior bonding. Encryption is
        // not needed for correctness; Windows will still pair (Just Works) automatically.
        service.addCharacteristic(readChar(UUID_HID_INFO, HID_INFO))
        service.addCharacteristic(readChar(UUID_REPORT_MAP, HidDescriptor.KEYBOARD))

        service.addCharacteristic(
            BluetoothGattCharacteristic(
                UUID_PROTOCOL_MODE,
                BluetoothGattCharacteristic.PROPERTY_READ or
                BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
                BluetoothGattCharacteristic.PERMISSION_READ or
                BluetoothGattCharacteristic.PERMISSION_WRITE
            ).apply { value = byteArrayOf(0x01) }
        )

        service.addCharacteristic(
            BluetoothGattCharacteristic(
                UUID_HID_CONTROL,
                BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE,
                BluetoothGattCharacteristic.PERMISSION_WRITE
            )
        )

        reportChar = BluetoothGattCharacteristic(
            UUID_REPORT,
            BluetoothGattCharacteristic.PROPERTY_NOTIFY or
            BluetoothGattCharacteristic.PROPERTY_READ,
            BluetoothGattCharacteristic.PERMISSION_READ
        ).apply {
            value = HidDescriptor.EMPTY_REPORT
            addDescriptor(
                BluetoothGattDescriptor(
                    UUID_CCCD,
                    BluetoothGattDescriptor.PERMISSION_READ or
                    BluetoothGattDescriptor.PERMISSION_WRITE
                ).apply { value = BluetoothGattDescriptor.DISABLE_NOTIFICATION_VALUE }
            )
            addDescriptor(
                BluetoothGattDescriptor(
                    UUID_REPORT_REF,
                    BluetoothGattDescriptor.PERMISSION_READ
                ).apply { value = byteArrayOf(0x00, 0x01) }
            )
        }
        service.addCharacteristic(reportChar)

        return service
    }

    private fun buildBatteryService(): BluetoothGattService {
        val service = BluetoothGattService(UUID_BATTERY_SERVICE,
            BluetoothGattService.SERVICE_TYPE_PRIMARY)
        service.addCharacteristic(readChar(UUID_BATTERY_LEVEL, byteArrayOf(100)))
        return service
    }

    private fun buildDevInfoService(): BluetoothGattService {
        val service = BluetoothGattService(UUID_DEVINFO_SERVICE,
            BluetoothGattService.SERVICE_TYPE_PRIMARY)
        service.addCharacteristic(readChar(UUID_PNP_ID, PNP_ID))
        return service
    }

    private fun readChar(uuid: UUID, value: ByteArray) =
        BluetoothGattCharacteristic(
            uuid,
            BluetoothGattCharacteristic.PROPERTY_READ,
            BluetoothGattCharacteristic.PERMISSION_READ
        ).apply { this.value = value }

    // ── Private: BLE advertising ──────────────────────────────────────────────

    private fun startAdvertising() {
        if (isAdvertising) return   // guard: don't call twice
        advertiser = adapter.bluetoothLeAdvertiser
        if (advertiser == null) {
            onStatus("⚠ BLE advertiser alınamadı (adapter kapalı?)")
            return
        }

        val settings = AdvertiseSettings.Builder()
            .setAdvertiseMode(AdvertiseSettings.ADVERTISE_MODE_LOW_LATENCY)
            .setConnectable(true)
            .setTimeout(0)                                       // advertise indefinitely
            .setTxPowerLevel(AdvertiseSettings.ADVERTISE_TX_POWER_MEDIUM)
            .build()

        // Primary ad: HID service UUID only.
        // Flags (3) + HID UUID (4) = 7 bytes — well under the 31-byte legacy limit.
        // No device name, no TX power, no scan response — avoids DATA_TOO_LARGE on any device.
        // Windows identifies the device as an HID keyboard via the UUID alone; it reads
        // the device name from the GATT Generic Access service after connecting.
        val adData = AdvertiseData.Builder()
            .setIncludeDeviceName(false)
            .setIncludeTxPowerLevel(false)
            .addServiceUuid(ParcelUuid(UUID_HID_SERVICE))
            .build()

        // Scan response: short name only ("Homeko" = 6 chars = 8 bytes, well under 31-byte limit).
        // This lets the device appear as "Homeko" in Windows "Add device" screen.
        val scanResp = AdvertiseData.Builder()
            .setIncludeDeviceName(true)
            .setIncludeTxPowerLevel(false)
            .build()

        advertiser?.startAdvertising(settings, adData, scanResp, advertiseCallback)
    }

    private fun stopAdvertising() {
        isAdvertising = false
        try {
            advertiser?.stopAdvertising(advertiseCallback)
        } catch (e: Exception) {
            Log.w(TAG, "stopAdvertising: ${e.message}")
        }
    }
}
