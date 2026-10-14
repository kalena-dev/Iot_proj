import json
import unittest
from fan_controller import FanController, SimulatedOutput, utc_iso


class FanTests(unittest.TestCase):
    def setUp(self):
        self.now = 1700000000.0
        self.output = SimulatedOutput()
        self.messages = []
        self.fan = FanController(self.output, lambda p: self.messages.append(json.loads(p)),
                                 simulated=True, max_run_seconds=10, clock=lambda: self.now)
        self.fan.connected()

    def command(self, on=True, command_id='test-command', age=15):
        return json.dumps({'commandId': command_id, 'on': on, 'expiresAt': utc_iso(self.now + age)})

    def test_confirmed_on_off(self):
        self.assertTrue(self.fan.apply(self.command()))
        self.assertEqual(self.output.value, 1)
        self.assertTrue(self.messages[-1]['on'])
        self.assertTrue(self.messages[-1]['simulated'])
        self.assertTrue(self.fan.apply(self.command(False, 'off-command')))
        self.assertEqual(self.output.value, 0)

    def test_retained_expired_malformed_commands_never_start(self):
        for command in [self.command(age=-1), self.command(age=100), self.command(on='true'), '{}', 'null', 'not json']:
            self.assertFalse(self.fan.apply(command))
        self.assertFalse(self.fan.apply(self.command(), retained=True))
        self.assertEqual(self.output.value, 0)

    def test_disconnect_and_reconnect_are_off(self):
        self.fan.apply(self.command())
        self.fan.disconnected()
        self.assertEqual(self.output.value, 0)
        self.assertFalse(self.fan.apply(self.command(command_id='offline-command')))
        self.fan.connected()
        self.assertFalse(self.messages[-1]['on'])

    def test_timeout_and_duplicate_do_not_extend_motor_run(self):
        payload = self.command()
        self.fan.apply(payload)
        self.now += 5
        self.assertFalse(self.fan.apply(payload))
        self.now += 6
        self.fan.tick()
        self.assertEqual(self.output.value, 0)
        self.assertIn('timeout', self.messages[-1]['reason'])


if __name__ == '__main__':
    unittest.main()
