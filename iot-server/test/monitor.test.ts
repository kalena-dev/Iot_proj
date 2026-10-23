import { test } from 'node:test';
import assert from 'node:assert/strict';
import { fixture } from './helpers.js';
const reading = (temperature: number, sampleId?: string) => JSON.stringify({ temperature, humidity: 62, sampleId });

test('four readings, independent saved thresholds and strict exceedance', async () => {
  const f = await fixture();
  await f.monitor.setThreshold('Frig1', 20);
  await f.monitor.setThreshold('Frig2', 25);
  await f.monitor.ingest('Frig1', reading(20));
  await f.monitor.ingest('Frig2', reading(26));
  const status = await f.monitor.snapshot();
  assert.deepEqual(status.refrigerators.map(r => [r.temperature, r.humidity, r.status]), [[20, 62, 'normal'], [26, 62, 'alert']]);
  assert.equal(f.emails.length, 1);
  assert.equal(f.emails[0].fridge, 'Frig2');
  assert.equal(f.commands.length, 0, 'An alert must not automatically turn on the fan');
  const restarted = await fixture(f.repo);
  assert.deepEqual((await restarted.monitor.snapshot()).refrigerators.map(r => r.threshold), [20, 25]);
});

test('invalid, unknown, retained and duplicated telemetry is not applied', async () => {
  const f = await fixture();
  assert.equal(await f.monitor.ingest('other', reading(22)), false);
  assert.equal(await f.monitor.ingest('Frig1', reading(22), true), false);
  await assert.rejects(f.monitor.ingest('Frig1', '{broken'));
  await assert.rejects(f.monitor.ingest('Frig1', '{"temperature":80,"humidity":60}'));
  await assert.rejects(f.monitor.ingest('Frig1', '{"temperature":20,"humidity":-1}'));
  assert.equal(await f.monitor.ingest('Frig1', reading(22, 'boot-1')), true);
  f.advance(10);
  assert.equal(await f.monitor.ingest('Frig1', reading(22, 'boot-1')), false);
  assert.equal(f.repo.readings.length, 1);
  assert.equal(f.emails.length, 1);
});

test('cooldown prevents floods and each refrigerator gets its own email', async () => {
  const f = await fixture();
  for (let i = 0; i < 10; i++) await f.monitor.ingest('Frig1', reading(22));
  await f.monitor.ingest('Frig2', reading(23));
  assert.equal(f.emails.length, 2);
  f.advance(901);
  await f.monitor.ingest('Frig1', reading(24));
  assert.equal(f.emails.length, 3);
});

test('only correlated, authorized YES commands fan; duplicate replies are harmless', async () => {
  const f = await fixture();
  await f.monitor.ingest('Frig1', reading(22));
  assert.equal(await f.monitor.reply('attacker@example.com', f.subject(), 'YES'), 'ignored');
  assert.equal(await f.monitor.reply('owner@example.com', 'Re: other', 'YES'), 'ignored');
  assert.equal(await f.monitor.reply('owner@example.com', f.subject(), 'YES please'), 'ignored');
  assert.equal(f.commands.length, 0);
  await Promise.all([f.monitor.reply('OWNER@example.com', f.subject(), '\n yes\r\n> quoted email'),
    f.monitor.reply('owner@example.com', f.subject(), 'YES')]);
  assert.equal(f.commands.length, 1);
  assert.equal(f.commands[0].on, true);
  const beforeAck = await f.monitor.snapshot();
  assert.equal(beforeAck.fan.on, false, 'Status is actual reported output, not requested state');
  assert.equal(beforeAck.fan.pending, true);
  await f.online(true, f.commands[0].commandId);
  const afterAck = await f.monitor.snapshot();
  assert.equal(afterAck.fan.on, true);
  assert.equal(afterAck.fan.pending, null);
  assert.ok(afterAck.notifications.some(n => n.message.includes('confirmed fan ON')));
});

test('NO, no reply, stale data, recovered temperatures and expired replies never start fan', async () => {
  const no = await fixture();
  await no.monitor.ingest('Frig1', reading(22));
  await no.monitor.reply('owner@example.com', no.subject(), 'NO');
  await no.monitor.reply('owner@example.com', no.subject(), 'YES');
  assert.equal(no.commands.length, 0);
  for (const mode of ['stale', 'recovered', 'expired']) {
    const f = await fixture();
    await f.monitor.ingest('Frig1', reading(22));
    if (mode === 'stale') f.advance(31);
    if (mode === 'recovered') await f.monitor.ingest('Frig1', reading(8));
    if (mode === 'expired') f.advance(901);
    await f.monitor.reply('owner@example.com', f.subject(), 'YES');
    assert.equal(f.commands.length, 0, mode);
  }
});

test('email failure notifies and retries without generating new alerts', async () => {
  const f = await fixture();
  f.failEmail(true);
  await f.monitor.ingest('Frig1', reading(22));
  assert.equal(f.repo.alerts.length, 1);
  assert.equal(f.repo.alerts[0].emailState, 'pending');
  assert.ok(f.repo.notices.some(n => n.message.includes('delivery failed')));
  f.failEmail(false);
  f.advance(61);
  await f.monitor.ingest('Frig1', reading(22));
  await f.monitor.tick();
  assert.equal(f.emails.length, 1);
  assert.equal(f.repo.alerts.length, 1);
  assert.equal(f.repo.alerts[0].emailState, 'sent');
});

test('offline fan, stale ON acknowledgement and command timeout cannot report false success', async () => {
  const f = await fixture();
  f.advance(31);
  await assert.rejects(f.monitor.requestFan(true), /offline/);
  const stale = await f.monitor.snapshot();
  assert.equal(stale.fan.on, null);
  assert.equal(stale.refrigerators[0].status, 'offline');
  await f.monitor.acceptFanStatus(JSON.stringify({ online: true, on: true, simulated: false,
    commandId: null, timestamp: '2020-01-01T00:00:00Z' }));
  assert.equal((await f.monitor.snapshot()).fan.on, null);
  await f.online();
  await f.monitor.acceptFanStatus(JSON.stringify({ online: false, on: false, simulated: false,
    commandId: null, timestamp: '2020-01-01T00:00:00Z' }));
  assert.equal((await f.monitor.snapshot()).fan.online, false, 'An MQTT last will is offline even if its timestamp is old');
  await f.online();
  await f.monitor.requestFan(true);
  await assert.rejects(f.monitor.requestFan(false), /awaiting/);
  f.advance(16);
  await f.monitor.tick();
  assert.equal((await f.monitor.snapshot()).fan.pending, null);
  assert.ok(f.repo.notices.some(n => n.message.includes('timed out')));
});

test('recipient changes cancel old approvals and never send queued mail to the previous recipient', async () => {
  for (const emailState of ['pending', 'sent'] as const) {
    const f = await fixture();
    await f.monitor.ingest('Frig1', reading(22));
    const token = f.subject();
    f.repo.alerts[0].recipient = 'previous@recipient.test';
    f.repo.alerts[0].emailState = emailState;
    const restarted = await fixture(f.repo);
    assert.equal(f.repo.alerts[0].decision, 'NO');
    assert.equal(await restarted.monitor.reply('previous@recipient.test', token, 'YES'), 'ignored');
    await restarted.monitor.ingest('Frig1', reading(23));
    assert.equal(restarted.emails.length, 1);
    assert.equal(restarted.emails[0].recipient, 'owner@example.com');
    assert.equal(restarted.commands.length, 0);
  }
});

test('reject invalid thresholds and failed publication leaves no pending command', async () => {
  const f = await fixture();
  await assert.rejects(f.monitor.setThreshold('Frig1', NaN));
  await assert.rejects(f.monitor.setThreshold('Frig1', 51));
  f.failPublish(true);
  await assert.rejects(f.monitor.requestFan(true));
  assert.equal((await f.monitor.snapshot()).fan.pending, null);
});
