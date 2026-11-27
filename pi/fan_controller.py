#!/usr/bin/env python3
"""MQTT fan actuator. GPIO is opened only by main(), never by tests/imports."""
import json
import os
import signal
import threading
import time
import ssl
from datetime import datetime, timezone
from pathlib import Path
from urllib.parse import urlparse


def utc_iso(now=None):
    return datetime.fromtimestamp(time.time() if now is None else now, timezone.utc).isoformat().replace('+00:00', 'Z')


class FanController:
    def __init__(self, output, publish, simulated=False, max_run_seconds=300,
                 clock=time.time, monotonic=time.monotonic, replay_file=None):
        self.output = output
        self.output.off()
        self.publish = publish
        self.simulated = simulated
        self.max_run_seconds = max_run_seconds
        self.clock = clock
        self.monotonic = monotonic
        self.replay_file = Path(replay_file) if replay_file else None
        self.online = False
        self.command_id = None
        self.deadline = None
        self.last_status = float('-inf')
        self.seen = {}
        if self.replay_file and self.replay_file.exists():
            saved = json.loads(self.replay_file.read_text())
            if not isinstance(saved, dict) or any(not isinstance(key, str) or
                    type(value) not in (int, float) for key, value in saved.items()):
                raise ValueError('Invalid fan replay journal; leave output disabled and inspect it')
            self.seen = saved
        self.lock = threading.RLock()

    def status(self, reason='heartbeat'):
        self.publish(json.dumps({
            'online': self.online, 'on': bool(self.output.value), 'simulated': self.simulated,
            'commandId': self.command_id, 'timestamp': utc_iso(self.clock()), 'reason': reason
        }))

    def connected(self):
        with self.lock:
            self.output.off()  # Never restore the previous motor state after reconnect.
            self.deadline = None
            self.command_id = None
            self.online = True
            self.status('connected; safe OFF')

    def disconnected(self):
        with self.lock:
            self.output.off()
            self.online = False
            self.deadline = None

    def apply(self, payload, retained=False):
        with self.lock:
            if retained or not self.online or len(payload) > 4096:
                return False
            try:
                command = json.loads(payload)
                command_id = command['commandId']
                on = command['on']
                expires_text = command['expiresAt']
                expires_date = datetime.fromisoformat(expires_text.replace('Z', '+00:00'))
                if expires_date.tzinfo is None:
                    return False
                expires = expires_date.timestamp()
                if not isinstance(command_id, str) or not 1 <= len(command_id) <= 80 or type(on) is not bool:
                    return False
                now = self.clock()
                if not now < expires <= now + 60:
                    return False
            except (KeyError, TypeError, ValueError, AttributeError, OverflowError):
                return False
            if not on:
                # OFF is idempotent and preempts even a replayed OFF command.
                self.output.off()
                self.deadline = None
                self.command_id = command_id
                self.status('GPIO output applied; OFF')
                return True
            if command_id in self.seen:
                self.status('duplicate command ignored')
                return False
            # Keep a full day beyond expiry so ordinary clock corrections cannot
            # revive commands. Fail closed rather than evict live replay entries.
            self.seen = {key: expiry for key, expiry in self.seen.items() if expiry >= now - 86400}
            if len(self.seen) >= 8192:
                return False
            self.seen[command_id] = expires
            if self.replay_file:
                self.replay_file.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
                temporary = self.replay_file.with_suffix('.tmp')
                with temporary.open('w') as journal:
                    json.dump(self.seen, journal)
                    journal.flush()
                    os.fsync(journal.fileno())
                temporary.replace(self.replay_file)
                if os.name != 'nt':
                    directory = os.open(self.replay_file.parent, os.O_RDONLY)
                    try:
                        os.fsync(directory)
                    finally:
                        os.close(directory)
            if self.deadline is not None and self.monotonic() >= self.deadline:
                self.output.off()
                self.deadline = None
                self.status('safety run timeout; OFF')
                return False
            if not self.output.value:
                self.output.on()
                self.deadline = self.monotonic() + self.max_run_seconds
            self.command_id = command_id
            self.status('GPIO output applied')
            return True

    def tick(self):
        with self.lock:
            now = self.monotonic()
            if self.deadline is not None and now >= self.deadline:
                self.output.off()
                self.deadline = None
                self.status('safety run timeout; OFF')
                self.last_status = now
            elif self.online and now - self.last_status >= 5:
                self.status()
                self.last_status = now


class SimulatedOutput:
    value = 0
    def on(self): self.value = 1
    def off(self): self.value = 0
    def close(self): self.off()


class MotorDriverOutput:
    """Enable + optional IN2 output; IN1 must be held LOW externally.

    Set direction before enabling. Disable the bridge before changing direction.
    The value confirms GPIO state only, never mechanical motor rotation.
    """
    def __init__(self, enable, in2=None):
        self.enable = enable
        self.in2 = in2
        self.closed = False
        self.off()

    @property
    def value(self):
        return int(not self.closed and bool(self.enable.value)
                   and (self.in2 is None or bool(self.in2.value)))

    def on(self):
        try:
            if self.in2 is not None:
                self.in2.on()
            self.enable.on()
        except Exception:
            try:
                self.off()
            except Exception:
                pass
            raise

    def off(self):
        if self.closed:
            return
        try:
            self.enable.off()
        finally:
            if self.in2 is not None:
                self.in2.off()

    def close(self):
        if self.closed:
            return
        try:
            self.off()
        finally:
            try:
                self.enable.close()
            finally:
                try:
                    if self.in2 is not None:
                        self.in2.close()
                finally:
                    self.closed = True


def validate_motor_config(environment):
    # ASP.NET configuration keys are case-insensitive, including GPIO__ENABLED.
    settings = {key.upper(): value for key, value in environment.items()}
    pin = int(settings.get('FAN_GPIO', '27'))
    in2_text = settings.get('FAN_IN2_GPIO', '17').strip()
    in2_pin = int(in2_text) if in2_text else None
    max_run = int(settings.get('FAN_MAX_RUN_SECONDS', '300'))
    pins = [pin] + ([] if in2_pin is None else [in2_pin])
    if any(number < 0 or number > 27 for number in pins):
        raise ValueError('Motor pins must be BCM GPIO numbers from 0 to 27')
    if len(set(pins)) != len(pins):
        raise ValueError('Motor enable and IN2 must use different GPIO pins')
    if not 1 <= max_run <= 3600:
        raise ValueError('FAN_MAX_RUN_SECONDS must be 1–3600')
    if settings.get('GPIO__ENABLED', 'true').strip().lower() != 'false':
        reserved = {int(settings.get('GPIO__BLUELEDPIN', '5')),
                    int(settings.get('GPIO__REDLEDPIN', '6'))}
        if settings.get('GPIO__BUZZERENABLED', 'true').strip().lower() != 'false':
            reserved.add(int(settings.get('GPIO__BUZZERPIN', '22')))
        if set(pins) & reserved:
            raise ValueError('Motor GPIO conflicts with status LEDs/buzzer. Set GPIO__ENABLED=false or relocate those status outputs.')
    return pin, in2_pin, max_run


def create_motor_output(pin, in2_pin):
    from gpiozero import DigitalOutputDevice
    from gpiozero.pins.lgpio import LGPIOFactory
    factory = LGPIOFactory()
    # Open EN first, LOW, so IN2 initialization can never energize the bridge.
    enable = DigitalOutputDevice(pin, active_high=True, initial_value=False, pin_factory=factory)
    in2 = None
    try:
        if in2_pin is not None:
            in2 = DigitalOutputDevice(in2_pin, active_high=True, initial_value=False, pin_factory=factory)
        return MotorDriverOutput(enable, in2)
    except Exception:
        try:
            enable.close()
        finally:
            if in2 is not None:
                in2.close()
        raise


def mqtt_connection_settings(environment):
    """Require authenticated TLS; hostname is the certificate SAN, not discovered IP."""
    parsed = urlparse(environment.get('MQTT_URL', 'mqtts://localhost:8883').strip())
    if parsed.scheme != 'mqtts' or not parsed.hostname or parsed.username or parsed.password:
        raise ValueError('MQTT_URL must use mqtts:// without embedded credentials')
    try:
        port = parsed.port or 8883
    except ValueError:
        raise ValueError('MQTT_URL must contain a valid broker port') from None
    if not 1 <= port <= 65535:
        raise ValueError('MQTT port must be 1–65535')
    host = environment.get('MQTT_SERVER_NAME', parsed.hostname).strip()
    if not host or '/' in host or ':' in host or any(character.isspace() for character in host):
        raise ValueError('MQTT_SERVER_NAME must be the broker certificate DNS name')
    username = environment.get('MQTT_USER', '').strip()
    password = environment.get('MQTT_PASSWORD', '')
    if username != 'northstar-fan' or len(password) < 20 or password.startswith(('YOUR_', 'CHANGE_')):
        raise ValueError('Configure dedicated northstar-fan MQTT credentials (password >=20 characters)')
    if not environment.get('MQTT_CA_FILE', '').strip():
        raise ValueError('MQTT_CA_FILE is required')
    return host, port, True, username, password


def main():
    import paho.mqtt.client as mqtt
    # systemd supplies only /etc/northstar/fan.env; never load shared DB/mail files.
    host, port, mqtt_tls, username, password = mqtt_connection_settings(os.environ)
    simulated = os.getenv('FAN_SIMULATE', 'true').lower() == 'true'
    pin, in2_pin, max_run = validate_motor_config(os.environ)
    if simulated:
        output = SimulatedOutput()
        print('FAN SIMULATION: no physical GPIO is being driven.', flush=True)
    else:
        if os.getenv('FAN_WIRING_CONFIRMED', 'false').strip().lower() != 'true':
            raise ValueError('Verify driver inputs, common GND and external motor power, then set FAN_WIRING_CONFIRMED=true. Never connect a GPIO to +Vmotor.')
        # GPIO Zero detects the Pi 5 RP1 chip; never silently simulate on failure.
        output = create_motor_output(pin, in2_pin)
        direction = f', IN2=BCM{in2_pin}' if in2_pin is not None else ''
        print(f'FAN REAL GPIO: EN=BCM{pin}{direction}; initialized safe OFF.', flush=True)
    client = mqtt.Client(mqtt.CallbackAPIVersion.VERSION2, client_id='northstar-pi-fan', clean_session=True)
    if username:
        client.username_pw_set(username, password)
    context = ssl.create_default_context(cafile=os.environ['MQTT_CA_FILE'])
    context.minimum_version = ssl.TLSVersion.TLSv1_2
    client.tls_set_context(context)
    client.reconnect_delay_set(min_delay=1, max_delay=10)
    client.will_set('store/fan/status', json.dumps({
        'online': False, 'on': False, 'simulated': simulated, 'commandId': None,
        'timestamp': utc_iso(), 'reason': 'MQTT connection lost'
    }), qos=1, retain=True)
    controller = FanController(output,
        lambda payload: client.publish('store/fan/status', payload, qos=1, retain=True),
        simulated=simulated, max_run_seconds=max_run,
        replay_file=os.getenv('FAN_REPLAY_FILE', '/var/lib/northstar-fan/commands.json'))
    stop = threading.Event()

    def on_connect(_client, _userdata, _flags, reason_code, _properties):
        if reason_code.is_failure:
            controller.disconnected()
            print('Fan MQTT sign-in rejected; check broker credentials.', flush=True)
            return
        controller.connected()
        client.subscribe('store/fan/set', qos=1)
        print('Fan MQTT connected; safe OFF status published.', flush=True)

    def on_connect_fail(_client, _userdata):
        controller.disconnected()
        print('Fan MQTT connection failed; output OFF. Check MQTT_URL/network/TLS; retrying.', flush=True)

    def on_disconnect(_client, _userdata, _flags, _reason, _properties):
        controller.disconnected()

    def on_message(_client, _userdata, message):
        try:
            if message.topic == 'store/fan/set':
                controller.apply(message.payload, retained=message.retain)
        except Exception:
            controller.disconnected()
            print('Fan output failed; forced OFF. Check GPIO wiring/permissions.', flush=True)

    client.on_connect = on_connect
    client.on_connect_fail = on_connect_fail
    client.on_disconnect = on_disconnect
    client.on_message = on_message
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, lambda _signum, _frame: stop.set())
    try:
        client.connect_async(host, port, keepalive=15)
        client.loop_start()
        while not stop.wait(0.25):
            controller.tick()
    finally:
        controller.disconnected()
        if client.is_connected():
            info = client.publish('store/fan/status', json.dumps({
                'online': False, 'on': False, 'simulated': simulated, 'commandId': None,
                'timestamp': utc_iso(), 'reason': 'controller stopped'
            }), qos=1, retain=True)
            info.wait_for_publish(timeout=2)
        client.disconnect()
        client.loop_stop()
        output.close()


if __name__ == '__main__':
    main()
