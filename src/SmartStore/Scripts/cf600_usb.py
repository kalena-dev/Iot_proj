"""Read passive EPC reports from the CF600 CDC bulk endpoint."""
import ctypes as c
from time import monotonic

usb = c.CDLL("libusb-1.0.so.0")
pointer = c.c_void_p
usb.libusb_open_device_with_vid_pid.argtypes = [pointer, c.c_uint16, c.c_uint16]
usb.libusb_open_device_with_vid_pid.restype = pointer
usb.libusb_bulk_transfer.argtypes = [pointer, c.c_uint8, pointer, c.c_int, c.POINTER(c.c_int), c.c_uint]
usb.libusb_claim_interface.argtypes = [pointer, c.c_int]
usb.libusb_release_interface.argtypes = [pointer, c.c_int]
usb.libusb_close.argtypes = [pointer]
usb.libusb_exit.argtypes = [pointer]

context = pointer()
if usb.libusb_init(c.byref(context)) != 0:
    raise SystemExit("USB initialization failed")
device = usb.libusb_open_device_with_vid_pid(context, 0x0483, 0x5750)
if not device:
    raise SystemExit("CF600 open failed")
claimed = []
try:
    for interface in (2, 3):
        status = usb.libusb_claim_interface(device, interface)
        if status != 0:
            raise SystemExit(f"Interface {interface} claim failed: {status}")
        claimed.append(interface)
    buf = c.create_string_buffer(4096)
    received = c.c_int()
    pending = bytearray()
    header = bytes.fromhex("0000cf010001")
    while True:
        status = usb.libusb_bulk_transfer(device, 0x87, buf, len(buf), c.byref(received), 600)
        if received.value:
            pending.extend(buf.raw[:received.value])
            while len(pending) >= 8:
                start = pending.find(header)
                if start < 0:
                    del pending[:-(len(header) - 1)]
                    break
                del pending[:start]
                if len(pending) < 8:
                    break
                payload_length = int.from_bytes(pending[6:8], "little")
                if not 8 <= payload_length <= 70:
                    del pending[0]
                    continue
                frame_length = payload_length + 9
                if len(pending) < frame_length:
                    break
                epc_length = pending[12]
                if 2 <= epc_length <= 64 and payload_length == epc_length + 6:
                    print(monotonic(), pending[13:13 + epc_length].hex().upper(), flush=True)
                del pending[:frame_length]
        if status not in (0, -7):
            raise SystemExit(f"Bulk read failed: {status}")
finally:
    for interface in reversed(claimed):
        usb.libusb_release_interface(device, interface)
    usb.libusb_close(device)
    usb.libusb_exit(context)
