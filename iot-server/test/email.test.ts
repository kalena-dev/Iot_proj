import { test } from 'node:test';
import assert from 'node:assert/strict';
import nodemailer, { type SendMailOptions } from 'nodemailer';
import { EmailService, type EmailConfig } from '../src/email.js';
import type { Alert } from '../src/model.js';

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
  assert.equal(sent, false);
  service.close();
});
