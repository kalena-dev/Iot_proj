import tempfile
import unittest
from pathlib import Path

from cf600_sdk import inventory, parse_tag


class SdkReaderTests(unittest.TestCase):
    record = 'Data:E2 80 11 91 A5 03 00 65 99 BF CC FD  Len:12 Ant:01 RSSI:-43 Channel:0'

    def test_respects_declared_length(self):
        self.assertEqual('E2801191A503006599BFCCFD', parse_tag(self.record))
        self.assertEqual('E2801191', parse_tag(self.record.replace('Len:12', 'Len:4')))
        for line in (self.record.replace('Len:12', 'Len:14'), self.record.replace('Len:12', 'Len:0'),
                     self.record.replace('CC FD', 'CC'), self.record.replace('E2', 'G2')):
            with self.subTest(line=line), self.assertRaises(ValueError):
                parse_tag(line)

    def inventory_fixture(self, lines):
        with tempfile.TemporaryDirectory(prefix='sdk with spaces ') as directory:
            cli = Path(directory) / 'CDemo.out'
            cli.write_text('#!/usr/bin/env python3\n'
                           'import os, sys\n'
                           'assert os.environ["LD_LIBRARY_PATH"].split(":")[0] == os.getcwd()\n'
                           'assert sys.stdin.read().splitlines() == ["a", "/dev/ttyUSB0", "6", "Q"]\n'
                           f'print({chr(10).join(lines)!r})\n')
            cli.chmod(0o700)
            return list(inventory(directory, '/dev/ttyUSB0'))

    def test_drives_menu_and_only_emits_unique_epcs(self):
        done = "Enter 'E' to continue, Enter 'Q' to exit!"
        self.assertEqual([['E2801191A503006599BFCCFD'], ['E2801191A503006599BFCCFE']],
                         self.inventory_fixture(['Open Success', self.record, self.record,
                                                 self.record.replace('CC FD', 'CC FE'), done]))
        self.assertEqual([], self.inventory_fixture(['Open Success', done]))

    def test_sdk_error_can_exit_zero(self):
        for line in ('Open Failed!FFFFFF12', 'Start Read Failed', 'Stop Read Failed', 'Open Success'):
            with self.subTest(line=line), self.assertRaises(ValueError):
                self.inventory_fixture([line])
