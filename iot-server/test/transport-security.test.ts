import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtemp, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import type { Db } from 'mongodb';
import { controlKeySchema, mqttTlsOptions } from '../src/transport-security.js';
import { OutboundEmailLog } from '../src/outbound-email-log.js';
import { fanCommandSchema, thresholdSchema } from '../src/model.js';

test('local API requires a generated nonplaceholder key, not merely a nonempty value', () => {
  for (const key of ['', 'short', 'a'.repeat(40), 'REPLACE_WITH_A_LONG_RANDOM_IOT_API_KEY', 'CHANGE_ME_TO_SOMETHING_LONG_ENOUGH'])
    assert.equal(controlKeySchema.safeParse(key).success, false);
  assert.equal(controlKeySchema.safeParse('mU1Z4vW6nT8sY0aB2cD5eF7gH9jK3pQr').success, true);
});

test('MQTT always uses a CA and certificate DNS identity and rejects plaintext', async t => {
  const directory = await mkdtemp(`${tmpdir()}/northstar-tls-`);
  t.after(() => rm(directory, { recursive: true, force: true }));
  const path = `${directory}/ca.pem`;
  await writeFile(path, 'synthetic CA bytes, no connection is attempted');
  assert.throws(() => mqttTlsOptions('mqtt://remote.invalid:1883', path, 'broker.local'), /mqtts/);
  assert.throws(() => mqttTlsOptions('mqtt://localhost:1883', path, 'broker.local'), /mqtts/);
  assert.throws(() => mqttTlsOptions('mqtts://user:secret@localhost:8883', path, 'broker.local'), /embedded/);
  assert.throws(() => mqttTlsOptions('mqtts://localhost:8883', path, '127.0.0.1'), /DNS/);
  assert.throws(() => mqttTlsOptions('mqtts://localhost:8883', '', 'broker.local'), /required/);
  const options = mqttTlsOptions('mqtts://127.0.0.1:8883', path, 'broker.local');
  assert.equal(options.rejectUnauthorized, true);
  assert.equal(options.servername, 'broker.local');
  assert.ok(Buffer.isBuffer(options.ca));
});

test('REST actor fields survive validation and malformed attribution is rejected', () => {
  const actor = { id: 'stable-account-id', name: 'Staff Name' };
  assert.deepEqual(fanCommandSchema.parse({ on: false, actor }).actor, actor);
  assert.deepEqual(thresholdSchema.parse({ threshold: 9, actor }).actor, actor);
  assert.equal(fanCommandSchema.safeParse({ on: true, actor: { id: '' } }).success, false);
});

test('email log never stores temperature approval subject or token-bearing reference', async () => {
  const records: Record<string, unknown>[] = [];
  const db = { collection() { return {
    async insertOne(record: Record<string, unknown>) { records.push(record); }, async updateOne() {}
  }; } } as unknown as Db;
  const log = new OutboundEmailLog(db);
  const token = '01aae540-9217-4b89-9bd4-160fc451e781';
  let sends = 0;
  await log.send('temperature-alert', 'sender@local.test', 'recipient@local.test',
    `Frig1 temperature alert [Northstar:${token}]`, async () => { sends++; }, token);
  assert.equal(sends, 1);
  assert.equal(records.length, 1);
  assert.doesNotMatch(JSON.stringify(records), new RegExp(token));
  assert.equal(records[0].Reference, undefined);
  assert.equal(records[0].Subject, 'Temperature alert [approval redacted]');
});
