package world.homeko.bridge

import android.util.Log
import kotlinx.coroutines.*
import kotlin.coroutines.coroutineContext
import java.io.BufferedReader
import java.io.InputStreamReader
import java.io.PrintWriter
import java.net.ServerSocket
import java.net.Socket

private const val TAG = "TcpServer"

class TcpServer(
    private val port: Int,
    private val hid: HidKeyboardService,
    private val onLog: (String) -> Unit,
) {
    private var serverSocket: ServerSocket? = null
    private var scope = CoroutineScope(Dispatchers.IO + SupervisorJob())

    // Batch collection state
    private val batchLines   = mutableListOf<String>()
    private var collectingBatch = false
    private var isLoopBatch     = false
    private var batchJob: Job?  = null

    fun start() {
        scope.launch {
            try {
                serverSocket = ServerSocket(port)
                onLog("TCP $port dinleniyor")
                while (isActive) {
                    val client = serverSocket!!.accept()
                    launch { handleClient(client) }
                }
            } catch (e: Exception) {
                if (isActive) onLog("TCP hata: ${e.message}")
            }
        }
    }

    private suspend fun handleClient(socket: Socket) {
        val addr = socket.inetAddress.hostAddress
        onLog("Bağlantı: $addr")
        try {
            val reader = BufferedReader(InputStreamReader(socket.getInputStream()))
            val writer = PrintWriter(socket.getOutputStream(), true)

            // Use readLine() loop instead of reader.lines() Stream — readLine() plays
            // nicer with coroutine cancellation and avoids Java 8 Stream quirks on Android.
            while (coroutineContext.isActive) {
                val line = reader.readLine() ?: break   // null = client disconnected
                val trimmed = line.trim()
                if (trimmed.isEmpty()) continue
                Log.d(TAG, "Recv: $trimmed")
                onLog("← $trimmed")

                val response = processCommand(trimmed)
                if (response != null) writer.println(response)
            }
        } catch (e: Exception) {
            onLog("Bağlantı kapandı: $addr")
        } finally {
            // Cancel any running batch when client disconnects
            batchJob?.cancel()
            batchJob        = null
            collectingBatch = false
            batchLines.clear()
            socket.close()
        }
    }

    private suspend fun processCommand(cmd: String): String? {
        return when {
            cmd == "PING" -> "PONG"

            // --- Batch protocol ---
            cmd == "EXEC" -> {
                collectingBatch = true; isLoopBatch = false; batchLines.clear(); null
            }
            cmd == "EXEC_LOOP" -> {
                collectingBatch = true; isLoopBatch = true; batchLines.clear(); null
            }
            cmd == "ENDEXEC" && collectingBatch -> {
                collectingBatch = false
                // Gate on HID connectivity before launching — gives the PC an immediate
                // error response instead of silently skipping every keystroke.
                if (!hid.isHidConnected) {
                    batchLines.clear()
                    onLog("✗ BT HID bağlı değil — batch iptal edildi")
                    return "BT_NOT_CONNECTED"
                }
                val snapshot = batchLines.toList()
                batchLines.clear()
                onLog("▶ Batch başlatılıyor: ${snapshot.size} olay, loop=$isLoopBatch")
                startBatchExecution(snapshot, isLoopBatch)
                null
            }
            // CANCEL takes priority — processed even while collecting
            cmd == "CANCEL" -> {
                batchJob?.cancel(); batchJob = null
                collectingBatch = false; batchLines.clear()
                null
            }
            collectingBatch -> {
                batchLines.add(cmd); null
            }

            // --- Real-time commands (used outside batch, e.g. modifier keys) ---
            cmd.startsWith("KEYDOWN:") -> {
                if (!hid.isHidConnected) return "BT_NOT_CONNECTED"
                hid.keyDown(cmd.removePrefix("KEYDOWN:"))
                null
            }
            cmd.startsWith("KEYUP:") -> {
                if (!hid.isHidConnected) return "BT_NOT_CONNECTED"
                hid.keyUp(cmd.removePrefix("KEYUP:"))
                null
            }
            cmd.startsWith("TAP:") -> {
                if (!hid.isHidConnected) return "BT_NOT_CONNECTED"
                val parts  = cmd.removePrefix("TAP:").split(":")
                val key    = parts[0]
                val holdMs = parts.getOrNull(1)?.toLongOrNull() ?: 50L
                hid.tap(key, holdMs)
                null
            }
            cmd.startsWith("HOLD:") -> {
                hid.hold(cmd.removePrefix("HOLD:")); null
            }
            cmd.startsWith("RELEASE:") -> {
                hid.release(cmd.removePrefix("RELEASE:")); null
            }

            else -> {
                Log.w(TAG, "Bilinmeyen komut: $cmd"); null
            }
        }
    }

    // Parses batch lines and executes them with Android-local timing.
    // Line format: KEYDOWN:key:absoluteMs  or  KEYUP:key:absoluteMs
    // Uses System.nanoTime() for sub-millisecond accuracy.
    private fun startBatchExecution(lines: List<String>, loop: Boolean) {
        batchJob?.cancel()
        batchJob = scope.launch {
            try {
                do {
                    val startNs = System.nanoTime()
                    for (line in lines) {
                        if (!isActive) break
                        val parts = line.split(":")
                        if (parts.size < 3) continue
                        val cmd      = parts[0]
                        val key      = parts[1]
                        val targetMs = parts[2].toLongOrNull() ?: continue

                        val elapsedMs = (System.nanoTime() - startNs) / 1_000_000L
                        val waitMs    = targetMs - elapsedMs
                        if (waitMs > 0) delay(waitMs)

                        // Per-keystroke check handles BT drops mid-execution gracefully
                        if (!hid.isHidConnected) {
                            onLog("✗ BT HID kesildi — batch durdu")
                            return@launch
                        }
                        when (cmd) {
                            "KEYDOWN" -> hid.keyDown(key)
                            "KEYUP"   -> hid.keyUp(key)
                        }
                    }
                    if (loop && isActive) delay(50L) // inter-iteration gap
                } while (loop && isActive)
            } catch (_: CancellationException) { }
        }
    }

    fun stop() {
        scope.cancel()
        serverSocket?.close()
    }
}
