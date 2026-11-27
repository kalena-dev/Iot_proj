import test from 'node:test';
import assert from 'node:assert/strict';
import nodemailer from 'nodemailer';
import type { ImapFlow } from 'imapflow';
import { EmailService, type EmailConfig } from '../src/email.js';
import { fixture } from './helpers.js';

const config: EmailConfig = { enabled: true, smtpHost: 'smtp.invalid', smtpPort: 587,
  smtpUser: 'sender@local.test', smtpPassword: 'synthetic-only', from: 'sender@local.test',
  imapHost: 'imap.invalid', imapPort: 993, imapUser: 'replies@local.test', imapPassword: 'synthetic-only' };

function inbox(messages: Map<number, Buffer | Error>) {
  let validity = 1n;
  const searches: object[] = [];
  const client = {
    get mailbox() { return { uidValidity: validity, uidNext: Math.max(0, ...messages.keys()) + 1 }; },
    on() {}, async connect() {}, async logout() {}, close() {},
    async getMailboxLock(_name: string, options: { readOnly?: boolean }) {
      assert.equal(options.readOnly, true); return { release() {} };
    },
    async search(query: { uid: string }) {
      searches.push(query);
      const [start, end] = query.uid.split(':').map(Number);
      return [...messages.keys()].filter(uid => uid >= start && uid <= end).reverse();
    },
    async fetchOne(uid: number, request: { size?: boolean }) {
      const message = messages.get(uid);
      if (message instanceof Error) throw message;
      return request.size ? { size: message?.length ?? 0 } : { source: message };
    },
    async messageFlagsAdd() { assert.fail('Read-only mailbox processing must not change flags'); }
  } as unknown as ImapFlow;
  const service = new EmailService(config, nodemailer.createTransport({ streamTransport: true, buffer: true }), () => client);
  return { service, searches, reset: () => { validity++; } };
}
const reply = (subject: string, text = 'YES') => Buffer.from(`From: owner@example.com\r\nSubject: ${subject}\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n${text}\r\n`);

test('older already-read approval precedes new invalid mail and is processed exactly once', async () => {
  const f = await fixture();
  await f.ingest('Frig1', '{"temperature":22,"humidity":62,"sampleId":"mail-1"}');
  const messages = new Map<number, Buffer | Error>([[1, reply(f.subject())]]);
  for (let uid = 2; uid <= 250; uid++) messages.set(uid, reply('unrelated message'));
  const box = inbox(messages);
  try {
    await box.service.poll(f.monitor, f.repo);
    assert.equal(f.commands.length, 1);
    assert.equal([...f.repo.cursors.values()][0].lastUid, 100);
    await box.service.poll(f.monitor, f.repo);
    await box.service.poll(f.monitor, f.repo);
    await box.service.poll(f.monitor, f.repo);
    assert.equal([...f.repo.cursors.values()][0].lastUid, 250);
    assert.equal(f.commands.length, 1);
    assert.deepEqual(box.searches[0], { uid: '1:100' });
    assert.equal(f.monitor.emailConnected, true);
  } finally { box.service.close(); }
});

test('one bad message is quarantined without starving a later valid NO', async () => {
  const f = await fixture();
  await f.ingest('Frig1', '{"temperature":22,"humidity":62,"sampleId":"mail-2"}');
  const box = inbox(new Map([[1, Buffer.from('Subject: invalid sender\\r\\n\\r\\nYES')], [2, reply(f.subject(), 'NO')]]));
  try {
    await box.service.poll(f.monitor, f.repo);
    assert.equal(f.repo.alerts[0].decision, 'NO');
    assert.equal(f.commands.length, 0);
    assert.ok(f.repo.notices.some(n => n.action === 'mail-quarantine'));
    assert.equal([...f.repo.cursors.values()][0].lastUid, 2);
  } finally { box.service.close(); }
});

test('UIDVALIDITY changes reset cursor while durable approval consumption prevents replay', async () => {
  const f = await fixture();
  await f.ingest('Frig1', '{"temperature":22,"humidity":62,"sampleId":"mail-3"}');
  const box = inbox(new Map([[1, reply(f.subject())]]));
  try {
    await box.service.poll(f.monitor, f.repo);
    box.reset();
    await box.service.poll(f.monitor, f.repo);
    assert.equal([...f.repo.cursors.values()][0].uidValidity, '2');
    assert.equal(f.commands.length, 1);
    assert.equal(box.searches.length, 2);
  } finally { box.service.close(); }
});

test('transient approval persistence failure does not advance past the reply', async () => {
  const f = await fixture();
  await f.ingest('Frig1', '{"temperature":22,"humidity":62,"sampleId":"mail-4"}');
  const box = inbox(new Map([[1, reply(f.subject())]]));
  const original = f.repo.consumeAlert.bind(f.repo);
  f.repo.consumeAlert = async () => { throw new Error('database unavailable'); };
  try {
    await box.service.poll(f.monitor, f.repo);
    assert.equal(f.repo.cursors.size, 0);
    assert.equal(f.commands.length, 0);
    f.repo.consumeAlert = original;
    await box.service.poll(f.monitor, f.repo);
    assert.equal(f.commands.length, 1);
    assert.equal([...f.repo.cursors.values()][0].lastUid, 1);
  } finally { f.repo.consumeAlert = original; box.service.close(); }
});

test('transient fetch failure retries durably without blocking newer UID windows', async () => {
  const f = await fixture();
  await f.ingest('Frig1', '{"temperature":22,"humidity":62,"sampleId":"mail-retry"}');
  const messages = new Map<number, Buffer | Error>([[1, new Error('transient fetch failure')]]);
  for (let uid = 2; uid <= 250; uid++) messages.set(uid, reply('unrelated'));
  const box = inbox(messages);
  try {
    await box.service.poll(f.monitor, f.repo);
    assert.equal([...f.repo.cursors.values()][0].lastUid, 100);
    assert.equal(f.repo.failures.size, 1);
    messages.set(1, reply(f.subject()));
    await box.service.poll(f.monitor, f.repo);
    assert.equal([...f.repo.cursors.values()][0].lastUid, 200);
    assert.equal(f.repo.failures.size, 0);
    assert.equal(f.commands.length, 1);
    await box.service.poll(f.monitor, f.repo);
    assert.equal(f.commands.length, 1);
  } finally { box.service.close(); }
});
