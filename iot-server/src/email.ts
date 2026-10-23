import nodemailer, { type Transporter } from 'nodemailer';
import { ImapFlow } from 'imapflow';
import { simpleParser } from 'mailparser';
import type { Alert, Mailer } from './model.js';
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
    private readonly imapFactory: (config: EmailConfig) => ImapFlow = createImapClient) {
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
        const lock = await client.getMailboxLock('INBOX');
        lock.release();
        return true;
      } catch (error) { onFailure?.(diagnoseEmailError('imap-inbox', error)); return false; }
      finally { await client.logout().catch(() => client.close()); }
    };
    const [smtpOk, imapOk] = await Promise.all([smtp(), imap()]);
    return { smtp: smtpOk, imap: imapOk };
  }
  async sendSetupTest(recipient: string) {
    if (!this.config.enabled) throw new Error('Email is disabled');
    await this.transport.sendMail({
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
    await this.transport.sendMail({
      from: this.config.from, to: alert.recipient, replyTo: this.config.imapUser,
      subject: `${alert.fridge} temperature alert [Northstar:${alert._id}]`,
      text: `The current temperature is ${alert.temperature} °C. Would you like to turn on the fan?\n\n` +
        `${alert.fridge} threshold: ${alert.threshold} °C.\n` +
        'Reply YES on the first line to turn on the shared store fan. Reply NO (or do not reply) to take no action.\n' +
        `Keep the subject unchanged. This request expires at ${alert.expiresAt}.\n` +
        'The dashboard will show ON only after the Raspberry Pi confirms the output.\n'
    });
  }
  async poll(monitor: Monitor) {
    if (!this.config.enabled) { monitor.emailConnected = false; return; }
    const client = this.imapFactory(this.config);
    client.on('error', () => { monitor.emailConnected = false; });
    try {
      await client.connect();
      const lock = await client.getMailboxLock('INBOX');
      try {
        monitor.emailConnected = true;
        // A dedicated mailbox is recommended. Never change unrelated messages.
        const uids = await client.search({ seen: false, subject: '[Northstar:' }, { uid: true });
        for (const uid of (uids || []).slice(-50)) {
          const header = await client.fetchOne(uid, { size: true }, { uid: true });
          if (!header || (header.size ?? 0) > 262144) continue;
          const message = await client.fetchOne(uid, { source: true }, { uid: true });
          if (!message || !message.source) continue;
          const parsed = await simpleParser(message.source);
          const addresses = parsed.from?.value ?? [];
          if (addresses.length !== 1 || !addresses[0].address) continue;
          const result = await monitor.reply(addresses[0].address, parsed.subject ?? '', parsed.text ?? '');
          if (result === 'processed') await client.messageFlagsAdd(uid, ['\\Seen'], { uid: true });
        }
      } finally { lock.release(); }
    } catch {
      monitor.emailConnected = false;
      console.warn('Email reply polling failed; check IMAP settings/network or fan controller availability.');
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
