#!/usr/bin/env python3
"""CF600 serial inventory -> JSON EPC batches for rfid_bridge.py.

Serial command and CF frame layout come from the classroom setup guide.
Only inventory is sent; no RF configuration or vendor SDK is used.
"""
import argparse
import json
import sys
import time

PORT = '/dev/ttyUSB0'
BAUD = 115200
READ_TAGS = bytes.fromhex('04 FF 01 1B B4')


def parse_epcs(buf):
    """Parse complete CF frames, retaining a fragmented final frame.

    CF | addr | cmd(2) | len | status | rssi(2) | ant | ? | epc_len
       | epc | crc(2)
    Like the classroom demo, this uses frame length/status, not CRC validation:
    the guide does not specify the response CRC algorithm.
    """
    epcs = []
    while buf:
        start = buf.find(b'\xcf')
        if start < 0:
            return epcs, b''
        buf = buf[start:]
        if len(buf) < 5:
            break
        length = buf[4]
        end = 5 + length + 2
        if len(buf) < end:
            break
        data = buf[5:5 + length]
        if length >= 6 and data[0] == 0:
            epc_length = data[5]
            # Never emit zero-length, truncated, or oversized identifiers.
            if 2 <= epc_length <= 64 and len(data) == 6 + epc_length:
                epcs.append(data[6:].hex().upper())
        buf = buf[end:]
    return list(dict.fromkeys(epcs)), buf


def inventory(reader, buf, pause=time.sleep):
    reader.write(READ_TAGS)
    pause(0.5)
    # A bounded read; parser retains fragments across inventory cycles.
    buf += reader.read(min(reader.in_waiting, 4096))
    return parse_epcs(buf)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--port', default=PORT)
    args = parser.parse_args()
    try:
        import serial  # Pi OS python3-serial; parser tests need no serial package.
        with serial.Serial(args.port, BAUD, timeout=0.5, write_timeout=1) as reader:
            print(f'Opened {args.port} at {BAUD}. Waiting for real RFID tags...', file=sys.stderr)
            buf = b''
            while True:
                epcs, buf = inventory(reader, buf)
                if epcs:
                    print(json.dumps(epcs), flush=True)
    except KeyboardInterrupt:
        return 0
    except BrokenPipeError:
        return 1
    except (ImportError, OSError) as error:
        print(f'CF600 reader stopped: {error}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
