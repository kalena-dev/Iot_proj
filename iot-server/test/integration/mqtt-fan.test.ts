import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { createServer } from 'node:net';
import { fileURLToPath } from 'node:url';
import mqtt from 'mqtt';
import { Monitor } from '../../src/monitor.js';
import { MemoryRepository } from '../helpers.js';
import type { Alert } from '../../src/model.js';

async function waitFor(check: () => boolean | Promise<boolean>) {
  const deadline = Date.now() + 12000;
  while (Date.now() < deadline) {
    if (await check()) return;
    await new Promise(resolve => setTimeout(resolve, 50));
  }
  throw new Error('Timed out waiting for MQTT / simulated Pi');
}

test('isolated Mosquitto: two sensor topics → alerts → YES → Pi acknowledgement → OFF', { timeout: 30000 }, async () => {
  const socket = createServer();
  await new Promise<void>(resolve => socket.listen(0, '127.0.0.1', resolve));
  const port = (socket.address() as { port: number }).port;
  await new Promise<void>(resolve => socket.close(() => resolve()));
  const directory = await mkdtemp(`${tmpdir()}/northstar-mqtt-`);
  await writeFile(`${directory}/mosquitto.conf`, `listener ${port} 127.0.0.1\nallow_anonymous true\npersistence false\n`);
  const broker = spawn('mosquitto', ['-c', `${directory}/mosquitto.conf`], { stdio: 'ignore' });
  const root = fileURLToPath(new URL('../../../', import.meta.url));
  const piPath = fileURLToPath(new URL('../../../pi/fan_controller.py', import.meta.url));
  const actuator = spawn('python3', [piPath], { cwd: root,
    env: { ...process.env, MQTT_HOST: '127.0.0.1', MQTT_PORT: String(port), MQTT_USER: '', MQTT_PASSWORD: '',
      MQTT_TLS: 'false', FAN_SIMULATE: 'true', FAN_MAX_RUN_SECONDS: '30' }, stdio: ['ignore', 'pipe', 'pipe'] });
  let actuatorLog = '';
  actuator.stdout.on('data', data => { actuatorLog += data; });
  actuator.stderr.on('data', data => { actuatorLog += data; });
  const client = mqtt.connect(`mqtt://127.0.0.1:${port}`, { reconnectPeriod: 100, connectTimeout: 1000 });
  const repo = new MemoryRepository();
  const emails: Alert[] = [];
  const monitor = new Monitor(repo, { async send(alert) { emails.push(structuredClone(alert)); } }, {
    async publish(command) { await client.publishAsync('store/fan/set', JSON.stringify(command), { qos: 1, retain: false }); }
  }, { defaultThreshold: 8, staleSeconds: 30, cooldownSeconds: 900, replyExpirySeconds: 900,
    emailRetrySeconds: 60, recipient: 'owner@example.com' });
  const errors: unknown[] = [];
  try {
    await monitor.initialize();
    await waitFor(() => client.connected);
    monitor.brokerConnected = true;
    await client.subscribeAsync(['Frig1', 'Frig2', 'store/fan/status'], { qos: 1 });
    client.on('message', (topic, payload, packet) => {
      const task = topic === 'store/fan/status' ? monitor.acceptFanStatus(payload.toString()) : monitor.ingest(topic, payload.toString(), packet.retain);
      void task.catch(error => errors.push(error));
    });
    await waitFor(async () => (await monitor.snapshot()).fan.online);
    assert.ok(actuatorLog.includes('FAN SIMULATION'), actuatorLog);
    assert.equal((await monitor.snapshot()).fan.simulated, true);
    await client.publishAsync('Frig1', '{"temperature":22,"humidity":60,"sampleId":"test-1"}');
    await client.publishAsync('Frig2', '{"temperature":24,"humidity":70,"sampleId":"test-2"}');
    await waitFor(() => emails.length === 2);
    assert.deepEqual((await monitor.snapshot()).refrigerators.map(r => r.humidity), [60, 70]);
    assert.equal((await monitor.snapshot()).fan.on, false);
    const alert = emails.find(a => a.fridge === 'Frig1')!;
    await monitor.reply('owner@example.com', `Re: [Northstar:${alert._id}]`, 'YES');
    await waitFor(async () => {
      const state = await monitor.snapshot(); return state.fan.on === true && state.fan.pending === null;
    });
    await monitor.requestFan(false);
    await waitFor(async () => {
      const state = await monitor.snapshot(); return state.fan.on === false && state.fan.pending === null;
    });
    assert.deepEqual(errors, []);
  } finally {
    actuator.kill('SIGTERM');
    await new Promise(resolve => setTimeout(resolve, 200));
    await client.endAsync(true);
    broker.kill('SIGTERM');
    await rm(directory, { recursive: true, force: true });
  }
});
