import io
import unittest
from unittest.mock import Mock, MagicMock, patch
from cf600_reader import parse_epcs, inventory, main


# Synthetic protocol fixtures, not captured hardware reads.
def frame(epc=b'\x11\x22\x33\x44', status=0, epc_length=None):
    data = bytes([status, 0, 0, 1, 0, len(epc) if epc_length is None else epc_length]) + epc
    return b'\xcf\x00\x00\x01' + bytes([len(data)]) + data + b'\x00\x00'


class Cf600Tests(unittest.TestCase):
    def test_noise_multiple_tags_and_duplicates(self):
        epcs, leftover = parse_epcs(b'noise' + frame() + frame(b'\x55\x66') + frame())
        self.assertEqual(['11223344', '5566'], epcs)
        self.assertEqual(b'', leftover)

    def test_every_possible_fragment_boundary(self):
        packet = frame()
        for split in range(len(packet)):
            with self.subTest(split=split):
                first, buf = parse_epcs(packet[:split])
                second, buf = parse_epcs(buf + packet[split:])
                self.assertEqual(['11223344'], first + second)
                self.assertEqual(b'', buf)

    def test_status_empty_and_truncated_epcs_are_not_tags(self):
        for packet in (frame(status=1), frame(b''), frame(epc_length=20), frame(b'\x11')):
            with self.subTest(packet=packet):
                self.assertEqual(([], b''), parse_epcs(packet))

    def test_inventory_uses_exact_classroom_command_and_preserves_fragment(self):
        reader = Mock()
        packet = frame()
        reader.in_waiting = len(packet) - 3
        reader.read.return_value = packet[3:]
        pause = Mock()
        epcs, buf = inventory(reader, packet[:3], pause)
        reader.write.assert_called_once_with(bytes.fromhex('04 FF 01 1B B4'))
        reader.read.assert_called_once_with(len(packet) - 3)
        pause.assert_called_once_with(0.5)
        self.assertEqual(['11223344'], epcs)
        self.assertEqual(b'', buf)

    def test_main_opens_working_port_and_emits_json_for_bridge(self):
        serial = MagicMock()
        output = io.StringIO()
        with patch.dict('sys.modules', {'serial': serial}), \
             patch('sys.argv', ['cf600_reader.py']), \
             patch('cf600_reader.inventory', side_effect=[(['11223344'], b''), KeyboardInterrupt]), \
             patch('sys.stdout', output), patch('sys.stderr', io.StringIO()):
            self.assertEqual(0, main())
        serial.Serial.assert_called_once_with('/dev/ttyUSB0', 115200, timeout=0.5, write_timeout=1)
        self.assertEqual('["11223344"]\n', output.getvalue())

    def test_no_response_emits_no_invented_epc(self):
        reader = Mock(in_waiting=0)
        reader.read.return_value = b''
        self.assertEqual(([], b''), inventory(reader, b'', lambda _: None))
