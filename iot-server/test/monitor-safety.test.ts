import test from 'node:test';
import assert from 'node:assert/strict';
import { fixture, MemoryRepository } from './helpers.js';
import type { CommandRecord, Reading } from '../src/model.js';

// Node 20 is supported; Promise.withResolvers is not available on that runtime.
function gate() {
  let resolve!: () => void;
  const promise = new Promise<void>(done => { resolve = done; });
  return { promise, resolve };
}
const reading = (temperature = 22, sampleId = 'sample-1') => JSON.stringify({ temperature, humidity: 62, sampleId });

test('consumed approval survives restart without a new ON and accepts original command acknowledgement', async () => {
  const f = await fixture();
  await f.ingest('Frig1', reading());
  await f.monitor.reply('owner@example.com', f.subject(), 'YES');
  const id = f.commands[0].commandId;
  const restarted = await fixture(f.repo);
  await restarted.monitor.reply('owner@example.com', f.subject(), 'YES');
  await restarted.monitor.tick();
  assert.equal(restarted.commands.length, 0);
  assert.equal((await restarted.monitor.snapshot()).fan.commandId, id);
  assert.equal((await restarted.monitor.snapshot()).fan.outcome, 'uncertain');
  await restarted.online(true, id);
  assert.equal((await restarted.monitor.snapshot()).fan.pending, null);
  assert.equal(f.repo.alerts[0].command?.state, 'confirmed');
});

test('crash after approval claim before publish cannot issue a new ON', async () => {
  const repo = new MemoryRepository();
  const f = await fixture(repo);
  await f.ingest('Frig1', reading());
  const original = repo.notification.bind(repo);
  repo.notification = async notice => {
    if (notice.action === 'fan-on') throw new Error('simulated process loss');
    await original(notice);
  };
  await assert.rejects(f.monitor.reply('owner@example.com', f.subject(), 'YES'));
  assert.equal(repo.alerts[0].decision, 'YES');
  assert.equal(repo.alerts[0].command?.state, 'queued');
  repo.notification = original;
  const restarted = await fixture(repo);
  await restarted.monitor.reply('owner@example.com', f.subject(), 'YES');
  await restarted.monitor.tick();
  assert.equal(restarted.commands.length, 0);
});

test('crash after publication preserves stable association and never republishes', async () => {
  const repo = new MemoryRepository();
  const f = await fixture(repo);
  await f.ingest('Frig1', reading());
  const original = repo.updateCommand.bind(repo);
  repo.updateCommand = async (command: CommandRecord) => {
    if (command.state === 'pending') throw new Error('simulated crash');
    await original(command);
  };
  await assert.rejects(f.monitor.reply('owner@example.com', f.subject(), 'YES'));
  assert.equal(f.commands.length, 1);
  repo.updateCommand = original;
  const restarted = await fixture(repo);
  await restarted.monitor.reply('owner@example.com', f.subject(), 'YES');
  assert.equal(restarted.commands.length, 0);
  assert.equal(repo.alerts[0].command?.commandId, f.commands[0].commandId);
});

test('stalled SMTP never holds readings, threshold updates or OFF', { timeout: 2000 }, async () => {
  const mail = gate();
  const f = await fixture(new MemoryRepository(), { send: async () => mail.promise });
  try {
    await f.monitor.ingest('Frig1', reading());
    await f.monitor.setThreshold('Frig1', 10);
    await f.monitor.ingest('Frig2', reading(24));
    await f.monitor.requestFan(true);
    await f.monitor.requestFan(false);
    assert.equal((await f.monitor.snapshot()).fan.pending, false);
  } finally { mail.resolve(); await f.monitor.flushEmail(); }
});

test('OFF preempts ON while broker acknowledgement is stalled', { timeout: 2000 }, async () => {
  const broker = gate(), started = gate();
  const f = await fixture(new MemoryRepository(), { publish: async command => {
    if (command.on) { started.resolve(); await broker.promise; }
  } });
  const on = f.monitor.requestFan(true);
  try {
    await started.promise;
    await f.monitor.requestFan(false, { id: 'operator-stop' });
    assert.equal((await f.monitor.snapshot()).fan.pending, false);
    assert.equal([...f.repo.commands.values()][0].state, 'superseded');
    assert.ok(f.repo.notices.some(n => n.actor?.id === 'operator-stop' && n.action === 'fan-off'));
  } finally { broker.resolve(); await on; }
});

test('burst telemetry is coalesced and delayed writes retain ingress time', async () => {
  const storage = gate();
  class SlowRepository extends MemoryRepository {
    override async saveReading(value: Reading) { await storage.promise; return super.saveReading(value); }
  }
  const repo = new SlowRepository();
  const f = await fixture(repo);
  const pending = f.monitor.ingest('Frig1', reading());
  for (let i = 0; i < 1000; i++) void f.monitor.ingest('Frig1', reading(24, `burst-${i}`));
  await f.monitor.setThreshold('Frig1', 10);
  f.advance(31);
  storage.resolve();
  await pending;
  assert.equal(repo.readings.length, 2);
  assert.equal(repo.readings.at(-1)?._id, 'Frig1:burst-999');
  assert.equal((await f.monitor.snapshot()).refrigerators[0].stale, true);
  assert.equal(f.emails.length, 0);
  assert.equal(f.commands.length, 0);
});

test('offline approval queues once; recovered reading cancels it before dispatch', async () => {
  const f = await fixture();
  await f.ingest('Frig1', reading());
  f.monitor.brokerConnected = false;
  assert.equal(await f.monitor.reply('owner@example.com', f.subject(), 'YES'), 'processed');
  assert.equal((await f.monitor.snapshot()).fan.pending, true);
  assert.equal(f.repo.alerts[0].decision, 'YES');
  assert.equal(f.commands.length, 0);
  await f.ingest('Frig1', reading(8, 'recovered'));
  f.monitor.brokerConnected = true;
  await f.online();
  await f.monitor.tick();
  assert.equal(f.commands.length, 0);
  assert.equal(f.repo.alerts[0].command?.state, 'cancelled');
});

test('a fast reply during SMTP completion consumes approval without a later save erasing it', async () => {
  const mail = gate();
  const f = await fixture(new MemoryRepository(), { send: async () => mail.promise });
  try {
    await f.monitor.ingest('Frig1', reading());
    const token = f.repo.alerts[0]._id;
    assert.equal(await f.monitor.reply('owner@example.com', `Re: [Northstar:${token}]`, 'YES'), 'processed');
    assert.equal(f.commands.length, 1);
    mail.resolve();
    await f.monitor.flushEmail();
    assert.equal(f.repo.alerts[0].decision, 'YES');
    assert.equal(f.repo.alerts[0].command?.commandId, f.commands[0].commandId);
  } finally { mail.resolve(); await f.monitor.flushEmail(); }
});

test('OFF preempts pending ON even after the controller or broker goes offline', async () => {
  const f = await fixture();
  await f.monitor.requestFan(true);
  f.monitor.brokerConnected = false;
  await f.monitor.requestFan(false, { id: 'stop-operator' });
  const state = await f.monitor.snapshot();
  assert.equal(state.fan.pending, false);
  assert.equal(state.fan.outcome, 'queued');
  assert.equal([...f.repo.commands.values()][0].state, 'superseded');
  assert.equal(f.commands.length, 1, 'Disconnected MQTT must not accept a queued offline publish');
  const repeated = await f.monitor.requestFan(false, { id: 'second-operator' });
  assert.equal(repeated.commandId, state.fan.commandId);
  assert.equal(f.repo.commands.size, 2);
  assert.ok(f.repo.notices.some(n => n.actor?.id === 'second-operator' && n.outcome === 'coalesced'));
});

test('an uncertain OFF after restart can be resent safely with its original command identity', async () => {
  const f = await fixture();
  const original = await f.monitor.requestFan(false);
  const restarted = await fixture(f.repo);
  assert.equal((await restarted.monitor.snapshot()).fan.outcome, 'uncertain');
  const retry = await restarted.monitor.requestFan(false, { id: 'retry-stop-operator' });
  assert.equal(retry.commandId, original.commandId);
  assert.equal(restarted.commands.length, 1);
  assert.equal(restarted.commands[0].on, false);
  assert.equal((await restarted.monitor.snapshot()).fan.pending, false);
});
