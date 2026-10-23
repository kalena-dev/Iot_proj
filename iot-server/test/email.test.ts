import { test } from 'node:test';
import assert from 'node:assert/strict';
import nodemailer, { type SendMailOptions, type Transporter } from 'nodemailer';
import type { ImapFlow } from 'imapflow';
import { EmailService, type EmailConfig } from '../src/email.js';
import type { Alert } from '../src/model.js';
import type { EmailDiagnostic } from '../src/email-diagnostics.js';

const config: EmailConfig = { enabled: true, smtpHost: 'smtp.example.com', smtpPort: 587,
  smtpUser: 'alerts@example.com', smtpPassword: 'test-only', from: 'alerts@example.com',
  imapHost: 'imap.example.com', imapPort: 993, imapUser: 'replies@example.com', imapPassword: 'test-only' };
const alert: Alert = { _id: '01aae540-9217-4b89-9bd4-160fc451e781', fridge: 'Frig1', temperature: 22,
  threshold: 20, recipient: 'owner@example.com', createdAt: '2025-10-15T12:00:00Z',
  expiresAt: '2025-10-15T12:15:00Z', emailState: 'pending' };

test('outgoing email contains lab question, recipient, reply mailbox, correlation token and YES/NO instructions', async () => {
  // Stream transport composes the actual email without external SMTP/network access.
  const transport = nodemailer.createTransport({ streamTransport: true, buffer: true });
  let message: SendMailOptions | undefined;
  transport.use('compile', (mail, done) => { message = mail.data; done(); });
  const service = new EmailService(config, transport);
  await service.send(alert);
  assert.equal(message!.to, alert.recipient);
  assert.equal(message!.replyTo, config.imapUser);
  assert.ok(message!.subject!.includes(`[Northstar:${alert._id}]`));
  assert.ok(String(message!.text).includes('The current temperature is 22 °C. Would you like to turn on the fan?'));
  assert.ok(String(message!.text).includes('Reply YES'));
  assert.ok(String(message!.text).includes('Reply NO'));
  service.close();
});

test('disabled email never sends through the transport', async () => {
  const transport = nodemailer.createTransport({ streamTransport: true, buffer: true });
  let sent = false;
  transport.use('compile', (_mail, done) => { sent = true; done(); });
  const service = new EmailService({ ...config, enabled: false }, transport);
  await assert.rejects(service.send(alert), /disabled/);
  await assert.rejects(service.sendSetupTest('owner@example.com'), /disabled/);
  assert.equal(sent, false);
  service.close();
});

test('setup test message is not an actionable temperature approval', async () => {
  const transport = nodemailer.createTransport({ streamTransport: true, buffer: true });
  let message: SendMailOptions | undefined;
  transport.use('compile', (mail, done) => { message = mail.data; done(); });
  const service = new EmailService(config, transport);
  await service.sendSetupTest('owner@example.com');
  assert.equal(message!.to, 'owner@example.com');
  assert.equal(message!.replyTo, config.imapUser);
  assert.doesNotMatch(message!.subject!, /\[Northstar:/);
  assert.match(String(message!.text), /replying to this test never controls the fan/);
  service.close();
});

for (const smtpOk of [true, false]) for (const imapOk of [true, false]) {
  test(`connection checks report SMTP=${smtpOk}, IMAP=${imapOk}, with no email or flag changes`, async () => {
    let sent = false, released = false, loggedOut = false;
    const transport = { async verify() { if (!smtpOk) throw new Error('test auth failure'); return true; },
      async sendMail() { sent = true; }, close() {} } as unknown as Transporter;
    const client = { on() {}, async connect() { if (!imapOk) throw new Error('test auth failure'); },
      async getMailboxLock() { return { release() { released = true; } }; },
      async logout() { loggedOut = true; }, close() {} } as unknown as ImapFlow;
    const service = new EmailService(config, transport, () => client);
    const errors: EmailDiagnostic[] = [];
    assert.deepEqual(await service.verifyConnections(error => errors.push(error)), { smtp: smtpOk, imap: imapOk });
    assert.equal(errors.length, Number(!smtpOk) + Number(!imapOk));
    assert.equal(errors.some(error => error.stage === 'smtp-sign-in'), !smtpOk);
    assert.equal(errors.some(error => error.stage === 'imap-inbox'), !imapOk);
    assert.equal(sent, false);
    assert.equal(released, imapOk);
    assert.equal(loggedOut, true);
    service.close();
  });
}
