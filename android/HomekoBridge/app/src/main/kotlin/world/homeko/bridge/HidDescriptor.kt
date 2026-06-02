package world.homeko.bridge

/**
 * Standard HID boot keyboard descriptor.
 * Report: [modifier_byte, reserved, key1, key2, key3, key4, key5, key6]
 */
object HidDescriptor {
    val KEYBOARD: ByteArray = byteArrayOf(
        0x05.toByte(), 0x01.toByte(),  // Usage Page (Generic Desktop)
        0x09.toByte(), 0x06.toByte(),  // Usage (Keyboard)
        0xA1.toByte(), 0x01.toByte(),  // Collection (Application)

        // Modifier keys
        0x05.toByte(), 0x07.toByte(),  //   Usage Page (Keyboard)
        0x19.toByte(), 0xE0.toByte(),  //   Usage Minimum (Left Ctrl)
        0x29.toByte(), 0xE7.toByte(),  //   Usage Maximum (Right GUI)
        0x15.toByte(), 0x00.toByte(),  //   Logical Minimum (0)
        0x25.toByte(), 0x01.toByte(),  //   Logical Maximum (1)
        0x75.toByte(), 0x01.toByte(),  //   Report Size (1)
        0x95.toByte(), 0x08.toByte(),  //   Report Count (8)
        0x81.toByte(), 0x02.toByte(),  //   Input (Data, Variable, Absolute)

        // Reserved byte
        0x95.toByte(), 0x01.toByte(),  //   Report Count (1)
        0x75.toByte(), 0x08.toByte(),  //   Report Size (8)
        0x81.toByte(), 0x01.toByte(),  //   Input (Constant)

        // Keycodes (6 simultaneous)
        0x95.toByte(), 0x06.toByte(),  //   Report Count (6)
        0x75.toByte(), 0x08.toByte(),  //   Report Size (8)
        0x15.toByte(), 0x00.toByte(),  //   Logical Minimum (0)
        0x25.toByte(), 0x65.toByte(),  //   Logical Maximum (101)
        0x05.toByte(), 0x07.toByte(),  //   Usage Page (Keyboard)
        0x19.toByte(), 0x00.toByte(),  //   Usage Minimum (0)
        0x29.toByte(), 0x65.toByte(),  //   Usage Maximum (101)
        0x81.toByte(), 0x00.toByte(),  //   Input (Data, Array, Absolute)

        0xC0.toByte()                  // End Collection
    )

    fun buildReport(modifier: Byte, vararg keys: Byte): ByteArray {
        val report = ByteArray(8)
        report[0] = modifier
        report[1] = 0x00
        keys.forEachIndexed { i, k -> if (i < 6) report[i + 2] = k }
        return report
    }

    val EMPTY_REPORT: ByteArray = ByteArray(8)
}
