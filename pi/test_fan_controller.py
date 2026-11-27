import json
import tempfile
from pathlib import Path
import unittest
from fan_controller import FanController, SimulatedOutput, MotorDriverOutput, validate_motor_config, mqtt_connection_settings, utc_iso


class FanTests(unittest.TestCase):
    def setUp(self):
        self.now = 1700000000.0
        self.monotonic = 100.0
        self.output = SimulatedOutput()
        self.messages = []
        self.fan = FanController(self.output, lambda p: self.messages.append(json.loads(p)),
                                 simulated=True, max_run_seconds=10, clock=lambda: self.now,
                                 monotonic=lambda: self.monotonic)
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
        self.monotonic += 5
        self.assertFalse(self.fan.apply(payload))
        self.now += 6
        self.monotonic += 6
        self.fan.tick()
        self.assertEqual(self.output.value, 0)
        self.assertIn('timeout', self.messages[-1]['reason'])

    def test_distinct_on_commands_cannot_extend_continuous_run(self):
        self.assertTrue(self.fan.apply(self.command()))
        original_deadline = self.fan.deadline
        self.monotonic += 9
        self.assertTrue(self.fan.apply(self.command(command_id='another-on')))
        self.assertEqual(self.fan.deadline, original_deadline)
        self.monotonic += 1
        self.fan.tick()
        self.assertEqual(self.output.value, 0)

    def test_on_at_elapsed_deadline_is_off_without_waiting_for_tick(self):
        self.fan.apply(self.command())
        self.monotonic += 10
        self.assertFalse(self.fan.apply(self.command(command_id='late-on')))
        self.assertEqual(self.output.value, 0)
        self.assertIsNone(self.fan.deadline)

    def test_forward_and_backward_wall_clock_jumps_do_not_change_cutoff(self):
        for jump in (-86400, 86400):
            with self.subTest(jump=jump):
                self.fan.connected()
                self.assertTrue(self.fan.apply(self.command(command_id=str(jump))))
                self.now += jump
                self.monotonic += 9
                self.fan.tick()
                self.assertEqual(self.output.value, 1)
                self.monotonic += 1
                self.fan.tick()
                self.assertEqual(self.output.value, 0)

    def test_off_preempts_and_is_idempotent(self):
        self.fan.apply(self.command())
        off = self.command(False, 'stop')
        self.assertTrue(self.fan.apply(off))
        self.assertTrue(self.fan.apply(off))
        self.assertEqual(self.output.value, 0)
        self.assertIsNone(self.fan.deadline)
        self.monotonic += 3
        self.assertTrue(self.fan.apply(self.command(command_id='new-run')))
        self.assertEqual(self.fan.deadline, self.monotonic + 10)

    def test_expiry_requires_timezone_and_off_is_not_retained(self):
        bad = json.loads(self.command())
        bad['expiresAt'] = '2023-11-14T22:13:35'
        self.assertFalse(self.fan.apply(json.dumps(bad)))
        self.fan.apply(self.command())
        self.assertFalse(self.fan.apply(self.command(False, 'stop'), retained=True))
        self.assertEqual(self.output.value, 1)
        self.assertTrue(self.fan.apply(self.command(False, 'stop')))

    def test_replay_protection_survives_restart_and_more_than_128_commands(self):
        with tempfile.TemporaryDirectory() as directory:
            journal = Path(directory) / 'commands.json'
            make = lambda: FanController(SimulatedOutput(), lambda _: None,
                clock=lambda: self.now, monotonic=lambda: self.monotonic,
                replay_file=journal)
            fan = make()
            fan.connected()
            for index in range(130):
                self.assertTrue(fan.apply(self.command(command_id=str(index))))
            fan.disconnected()
            restarted = make()
            restarted.connected()
            self.assertFalse(restarted.apply(self.command(command_id='0')))
            self.assertEqual(restarted.output.value, 0)

    def test_failed_replay_write_never_energizes_output(self):
        with tempfile.TemporaryDirectory() as directory:
            journal = Path(directory) / 'commands.json'
            fan = FanController(SimulatedOutput(), lambda _: None,
                clock=lambda: self.now, replay_file=journal)
            fan.connected()
            journal.mkdir()
            with self.assertRaises(OSError):
                fan.apply(self.command())
            self.assertEqual(fan.output.value, 0)

    def test_invalid_replay_journal_initializes_safe_off(self):
        with tempfile.TemporaryDirectory() as directory:
            journal = Path(directory) / 'commands.json'
            journal.write_text('[]')
            output = SimulatedOutput()
            output.on()
            with self.assertRaises(ValueError):
                FanController(output, lambda _: None, replay_file=journal)
            self.assertEqual(output.value, 0)



class TrackedOutput:
    def __init__(self, name, events, fail_on=False):
        self.name, self.events, self.fail_on = name, events, fail_on
        self.value = 0
        self.closed = False

    def on(self):
        self.events.append(self.name + '.on')
        if self.fail_on:
            raise RuntimeError('test output failure')
        self.value = 1

    def off(self):
        self.events.append(self.name + '.off')
        self.value = 0

    def close(self):
        self.events.append(self.name + '.close')
        self.closed = True


class MotorDriverTests(unittest.TestCase):
    def output(self, fail_enable=False):
        events = []
        enable = TrackedOutput('EN27', events, fail_on=fail_enable)
        in2 = TrackedOutput('IN2-17', events)
        return MotorDriverOutput(enable, in2), events

    def test_l293d_direction_before_enable_and_disable_before_direction(self):
        output, events = self.output()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off'])
        events.clear()
        output.on()
        self.assertEqual(events, ['IN2-17.on', 'EN27.on'])
        self.assertEqual(output.value, 1)
        events.clear()
        output.off()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off'])
        self.assertEqual(output.value, 0)

    def test_failed_enable_rolls_back_both_outputs(self):
        output, events = self.output(fail_enable=True)
        events.clear()
        with self.assertRaises(RuntimeError):
            output.on()
        self.assertEqual(events, ['IN2-17.on', 'EN27.on', 'EN27.off', 'IN2-17.off'])
        self.assertEqual(output.value, 0)

    def test_close_is_off_and_closes_both_lines(self):
        output, events = self.output()
        output.on()
        events.clear()
        output.close()
        self.assertEqual(events, ['EN27.off', 'IN2-17.off', 'EN27.close', 'IN2-17.close'])
        self.assertTrue(output.enable.closed)
        self.assertTrue(output.in2.closed)
        self.assertEqual(output.value, 0)
        output.close()
        self.assertEqual(len(events), 4)

    def test_legacy_enable_only_output(self):
        events = []
        output = MotorDriverOutput(TrackedOutput('EN23', events))
        output.on()
        self.assertEqual(output.value, 1)
        output.off()
        self.assertEqual(output.value, 0)

    def test_disconnect_and_timeout_disable_both_real_output_roles(self):
        output, events = self.output()
        now = [1700000000.0]
        fan = FanController(output, lambda _: None, max_run_seconds=5,
                            clock=lambda: now[0], monotonic=lambda: now[0])
        fan.connected()
        command = lambda token: json.dumps({'commandId': token, 'on': True, 'expiresAt': utc_iso(now[0] + 15)})
        fan.apply(command('first'))
        now[0] += 6
        fan.tick()
        self.assertEqual(output.enable.value, 0)
        self.assertEqual(output.in2.value, 0)
        fan.apply(command('second'))
        fan.disconnected()
        self.assertEqual(output.enable.value, 0)
        self.assertEqual(output.in2.value, 0)

    def test_gpio17_27_keep_status_outputs_on_relocated_defaults(self):
        settings = {'FAN_GPIO': '27', 'FAN_IN2_GPIO': '17'}
        self.assertEqual(validate_motor_config(settings), (27, 17, 300))
        with self.assertRaisesRegex(ValueError, 'conflicts'):
            validate_motor_config({**settings, 'GPIO__BLUELEDPIN': '17'})
        self.assertEqual(validate_motor_config({**settings, 'GPIO__ENABLED': 'false'}), (27, 17, 300))

    def test_duplicate_invalid_or_custom_conflicting_pins_are_rejected(self):
        for settings in [
            {'FAN_GPIO': '23', 'FAN_IN2_GPIO': '23'},
            {'FAN_GPIO': '28'}, {'FAN_GPIO': '-1'}, {'FAN_MAX_RUN_SECONDS': '0'},
            {'FAN_GPIO': '5', 'GPIO__BLUELEDPIN': '5'}
        ]:
            with self.assertRaises(ValueError):
                validate_motor_config(settings)
        self.assertEqual(validate_motor_config({}), (27, 17, 300))


class MqttSettingsTests(unittest.TestCase):
    def settings(self, **overrides):
        return {'MQTT_URL': 'mqtts://localhost:8883', 'MQTT_USER': 'northstar-fan',
                'MQTT_PASSWORD': 'distinct-test-password-not-real',
                'MQTT_CA_FILE': '/test/ca.crt',
                'MQTT_SERVER_NAME': 'broker.test', **overrides}

    def test_certificate_server_name_overrides_location_not_validation(self):
        self.assertEqual(mqtt_connection_settings(self.settings()),
                         ('broker.test', 8883, True, 'northstar-fan', 'distinct-test-password-not-real'))
        self.assertEqual(mqtt_connection_settings(self.settings(MQTT_URL='mqtts://192.0.2.1:2883'))[:3],
                         ('broker.test', 2883, True))

    def test_insecure_missing_and_shared_credentials_rejected(self):
        for override in [
            {'MQTT_URL': 'mqtt://localhost'}, {'MQTT_URL': 'mqtt://remote.test'},
            {'MQTT_URL': 'mqtts://user:secret@broker.test'}, {'MQTT_URL': 'https://broker.test'},
            {'MQTT_URL': 'mqtts://'}, {'MQTT_URL': 'mqtts://broker.test:65536'},
            {'MQTT_URL': 'mqtts://broker.test:invalid'}, {'MQTT_CA_FILE': ''},
            {'MQTT_USER': 'northstar'}, {'MQTT_PASSWORD': 'short'},
            {'MQTT_SERVER_NAME': ''}
        ]:
            with self.subTest(override=override), self.assertRaises(ValueError):
                mqtt_connection_settings(self.settings(**override))
        with self.assertRaises(ValueError):
            mqtt_connection_settings({})


if __name__ == '__main__':
    unittest.main()
