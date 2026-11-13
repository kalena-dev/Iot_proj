import { randomUUID } from 'node:crypto';
import type { Db } from 'mongodb';

interface EmailRecord {
  _id: string; Kind: string; Subject: string; Sender: string; Recipient: string;
  Reference?: string; Status: string; CreatedAtUtc: Date; CompletedAtUtc?: Date;
}

export class OutboundEmailLog {
  constructor(private readonly db: Db) {}

  async send(kind: string, from: string, to: string, subject: string, submit: () => Promise<unknown>, reference?: string) {
    const records = this.db.collection<EmailRecord>('email_log');
    const _id = randomUUID();
    await records.insertOne({ _id, Kind: kind, Sender: from, Recipient: to, Subject: subject,
      ...(reference ? { Reference: reference } : {}), CreatedAtUtc: new Date(), Status: 'pending' });
    try {
      await submit();
    } catch (error) {
      await records.updateOne({ _id }, { $set: { Status: 'failed', CompletedAtUtc: new Date() } });
      throw error;
    }
    // SMTP already accepted it. A log outage must not trigger another send.
    try {
      await records.updateOne({ _id }, { $set: { Status: 'sent', CompletedAtUtc: new Date() } });
    } catch { console.error('Email accepted; audit outcome pending for ' + _id); }
  }
}
