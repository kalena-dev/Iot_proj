import { createHash } from 'node:crypto';
import nodemailer, { type Transporter } from 'nodemailer';
import type { OutboundEmailLog } from './outbound-email-log.js';
import { ImapFlow } from 'imapflow';
import { simpleParser } from 'mailparser';
import type { Alert, Mailer, Repository } from './model.js';
import type { Monitor } from './monitor.js';
import { diagnoseEmailError, type EmailDiagnostic } from './email-diagnostics.js';

export interface EmailConfig {
  enabled: boolean; smtpHost: string; smtpPort: number; smtpUser: string; smtpPassword: string;
  from: string; imapHost: string; imapPort: number; imapUser: string; imapPassword: string;
}
export interface EmailConnectionChecks { smtp: boolean; imap: boolean }
export class EmailService implements Mailer {
  private readonly transport: Transporter;
  constructor(private readonly config: EmailConfig, transport?: Transporter,
    private readonly imapFactory: (config: EmailConfig) => ImapFlow = createImapClient,
    private readonly log?: OutboundEmailLog) {
    this.transport = transport ?? nodemailer.createTransport({
      host: config.smtpHost, port: config.smtpPort, secure: config.smtpPort === 465,
      requireTLS: config.smtpPort !== 465,
      auth: { user: config.smtpUser, pass: config.smtpPassword },
      connectionTimeout: 15000, greetingTimeout: 20000, socketTimeout: 30000
    });
  }
  // Authentication/inbox access checks only: no email sent or flags changed.
  async verifyConnections(onFailure?: (diagnostic: EmailDiagnostic) => void): Promise<EmailConnectionChecks> {
    const smtp = async () => {
      try { await this.transport.verify(); return true; }
      catch (error) { onFailure?.(diagnoseEmailError('smtp-sign-in', error)); return false; }
    };
    const imap = async () => {
      const client = this.imapFactory(this.config);
      client.on('error', () => {});
      try {
        await client.connect();
        const lock = await client.getMailboxLock('INBOX', { readOnly: true });
        lock.release();
        return true;
      } catch (error) { onFailure?.(diagnoseEmailError('imap-inbox', error)); return false; }
      finally { await client.logout().catch(() => client.close()); }
    };
    const [smtpOk, imapOk] = await Promise.all([smtp(), imap()]);
    return { smtp: smtpOk, imap: imapOk };
  }
  private async submit(kind: string, message: {from: string; to: string; replyTo: string; subject: string; text: string}, reference?: string) {
    const submit = () => this.transport.sendMail(message);
    if (this.log) await this.log.send(kind, message.from, message.to, message.subject, submit, reference);
    else await submit();
  }
  async sendSetupTest(recipient: string) {
    if (!this.config.enabled) throw new Error('Email is disabled');
    await this.submit('setup-test', {
      from: this.config.from, to: recipient, replyTo: this.config.imapUser,
      subject: 'Northstar email setup test',
      text: 'The Northstar application successfully submitted this setup test through SMTP.\n\n' +
        'SMTP sign-in and IMAP inbox access were verified during setup.\n' +
        'This is not a temperature alert; replying to this test never controls the fan.\n' +
        'Future temperature alerts include a [Northstar:...] token. Keep that subject when replying YES or NO.\n' +
        'A real fan still requires the separate Raspberry Pi controller and verified wiring.\n'
    });
  }
  async send(alert: Alert) {
    if (!this.config.enabled) throw new Error('Email is disabled');
    await this.submit('temperature-alert', {
      from: this.config.from, to: alert.recipient, replyTo: this.config.imapUser,
      subject: `${alert.fridge} temperature alert [Northstar:${alert._id}]`,
      text: `The current temperature is ${alert.temperature} °C. Would you like to turn on the fan?\n\n` +
        `${alert.fridge} threshold: ${alert.threshold} °C.\n` +
        'Reply YES on the first line to turn on the shared store fan. Reply NO (or do not reply) to take no action.\n' +
        `Keep the subject unchanged. This request expires at ${alert.expiresAt}.\n` +
        'The dashboard will show ON only after the Raspberry Pi confirms the output.\n'
    }, alert._id);
  }
  async poll(monitor: Monitor, repo: Repository) {
    if (!this.config.enabled) { monitor.emailConnected = false; return; }
    const client = this.imapFactory(this.config);
    client.on('error', () => { monitor.emailConnected = false; });
    try {
      await client.connect();
      const lock = await client.getMailboxLock('INBOX', { readOnly: true });
      try {
        monitor.emailConnected = true;
        if (!client.mailbox) throw new Error('Inbox not selected');
        const id = createHash('sha256').update(`${this.config.imapHost}:${this.config.imapUser}:INBOX`).digest('hex');
        const validity = String(client.mailbox.uidValidity);
        if (!/^[1-9]\d*$/.test(validity) || !Number.isSafeInteger(client.mailbox.uidNext) || client.mailbox.uidNext < 1)
          throw new Error('Invalid mailbox UID metadata');
        const saved = await repo.mailboxCursor(id);
        const cursor = saved?.uidValidity === validity ? saved : { _id: id, uidValidity: validity, lastUid: 0 };
        const end = Math.min(cursor.lastUid + 100, client.mailbox.uidNext - 1);
        const retries = await repo.mailboxFailures(id, validity);
        if (end <= cursor.lastUid && retries.length === 0) return;
        const uids = end > cursor.lastUid
          ? await client.search({ uid: `${cursor.lastUid + 1}:${end}` }, { uid: true }) : [];
        for (const uid of [...new Set([...(uids || []), ...retries.map(f => f.uid)])].sort((a, b) => a - b)) {
          const failureId = `${id}:${validity}:${uid}`;
          let subject = '', sender = '', text = '', malformed = false, parsing = false, fetchFailed = false;
          try {
            const header = await client.fetchOne(uid, { size: true }, { uid: true });
            if (!header || (header.size ?? 0) > 262144) malformed = true;
            else {
              const message = await client.fetchOne(uid, { source: true }, { uid: true });
              if (!message || !message.source) malformed = true;
              else {
                parsing = true;
                const parsed = await simpleParser(message.source);
                const addresses = parsed.from?.value ?? [];
                if (addresses.length !== 1 || !addresses[0].address) malformed = true;
                else { sender = addresses[0].address; subject = parsed.subject ?? ''; text = parsed.text ?? ''; }
              }
            }
          } catch { if (parsing) malformed = true; else fetchFailed = true; }
          // Quarantine one malformed UID, not the whole pass. Database failures
          // propagate without advancing past a potentially unconsumed approval.
          if (fetchFailed) {
            await repo.saveMailboxFailure({ _id: failureId, mailbox: id, uidValidity: validity, uid });
          } else {
            if (malformed) await repo.notification({ _id: `mail:${id}:${validity}:${uid}`,
              createdAt: new Date().toISOString(), kind: 'error', message: 'Mailbox message quarantined.',
              action: 'mail-quarantine', outcome: 'invalid-message' });
            else if (/\[Northstar:/i.test(subject)) await monitor.reply(sender, subject, text);
            await repo.clearMailboxFailure(failureId);
          }
          cursor.lastUid = Math.max(cursor.lastUid, uid);
          await repo.saveMailboxCursor(cursor);
        }
        cursor.lastUid = Math.max(cursor.lastUid, end);
        await repo.saveMailboxCursor(cursor);
      } finally { lock.release(); }
    } catch {
      monitor.emailConnected = false;
      console.warn('Email reply polling failed; check IMAP settings/network or database availability.');
    } finally {
      await client.logout().catch(() => client.close());
    }
  }
  close() { this.transport.close(); }
}

function createImapClient(config: EmailConfig) {
  return new ImapFlow({
    host: config.imapHost, port: config.imapPort, secure: true,
    auth: { user: config.imapUser, pass: config.imapPassword },
    logger: false, socketTimeout: 20000, connectionTimeout: 10000, greetingTimeout: 10000
  });
}
