package world.homeko.bridge

import android.annotation.SuppressLint
import android.content.Context
import android.util.Log
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

private const val TAG = "HidKeyboard"

/**
 * High-level keyboard abstraction used by [TcpServer].
 *
 * Backed by [BleHidGattServer] (BLE HOGP) instead of the Classic BT
 * [android.bluetooth.BluetoothHidDevice] profile.
 *
 * BLE jitter: ±2–3 ms vs ±10–25 ms for Classic BT — enables reliable
 * sub-400 ms combo timing that previously required 500 ms with Classic BT.
 *
 * Public interface is identical to the previous Classic-BT implementation
 * so [TcpServer] and [BridgeForegroundService] need no changes.
 */
@SuppressLint("MissingPermission")
class HidKeyboardService(context: Context) {

    private val scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    // Modifier state for HOLD/RELEASE commands
    private var modifierByte: Byte = 0x00

    // Regular key held via KEYDOWN — released by KEYUP
    private var heldRegularKey: Byte = 0x00

    var onStatusChanged: ((String) -> Unit)? = null

    private val bleServer = BleHidGattServer(context) { msg ->
        onStatusChanged?.invoke(msg)
    }

    /** True when a BLE central is connected AND has subscribed to HID notifications. */
    val isHidConnected: Boolean get() = bleServer.isConnected

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    /** Open GATT server and start BLE advertising.  Call from [BridgeForegroundService]. */
    fun register() {
        Log.i(TAG, "BLE HID register() — açılıyor")
        bleServer.start()
    }

    /** Stop advertising and close GATT server. */
    fun unregister() {
        Log.i(TAG, "BLE HID unregister() — kapatılıyor")
        bleServer.stop()
    }

    // ── Key commands ──────────────────────────────────────────────────────────

    /** Press a key, hold for [holdMs] ms, then release (restores any held key). */
    suspend fun tap(keyName: String, holdMs: Long = 50L) {
        val usage = resolveKey(keyName) ?: return
        sendReport(modifierByte, usage)
        delay(holdMs.coerceAtLeast(10L))
        sendReport(modifierByte, heldRegularKey)   // restore held key (or 0x00 if none)
    }

    /** Press and hold a regular key; stays down until [keyUp] is called. */
    fun keyDown(keyName: String) {
        val usage = resolveKey(keyName) ?: return
        heldRegularKey = usage
        sendReport(modifierByte, heldRegularKey)
    }

    /** Release a previously held regular key. */
    fun keyUp(keyName: String) {
        val usage = resolveKey(keyName) ?: return
        if (heldRegularKey == usage) {
            heldRegularKey = 0x00
            sendReport(modifierByte, 0x00)
        }
    }

    /** Hold a modifier key (affects subsequent taps and held keys). */
    fun hold(modName: String) {
        modifierByte = (modifierByte.toInt() or KeyMap.modifierBit(modName).toInt()).toByte()
        sendReport(modifierByte, heldRegularKey)
    }

    /** Release a modifier key. */
    fun release(modName: String) {
        modifierByte = (modifierByte.toInt() and KeyMap.modifierBit(modName).toInt().inv()).toByte()
        sendReport(modifierByte, heldRegularKey)
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private fun sendReport(modifier: Byte, key: Byte) {
        val report = HidDescriptor.buildReport(modifier, key)
        bleServer.sendReport(report)
        Log.d(TAG, "sendReport mod=0x${modifier.toString(16)} key=0x${key.toString(16)}")
    }

    private fun resolveKey(name: String): Byte? {
        val usage = KeyMap.resolve(name)
        if (usage == null) {
            Log.w(TAG, "Unknown key: $name")
            onStatusChanged?.invoke("⚠ Bilinmeyen tuş: $name")
        }
        return usage
    }
}
