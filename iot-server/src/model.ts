import { z } from 'zod';

export const fridgeIds = ['Frig1', 'Frig2'] as const;
export type FridgeId = typeof fridgeIds[number];
export const telemetrySchema = z.object({
  temperature: z.number().finite().min(0).max(50),
  humidity: z.number().finite().min(0).max(100),
  sampleId: z.string().regex(/^[a-zA-Z0-9_-]{1,80}$/)
});
export const actorSchema = z.object({ id: z.string().trim().min(1).max(128), name: z.string().trim().max(120).optional() });
export type Actor = z.infer<typeof actorSchema>;
export const thresholdSchema = z.object({ threshold: z.number().finite().min(0).max(50), actor: actorSchema.optional() });
export const fanCommandSchema = z.object({ on: z.boolean(), actor: actorSchema.optional() });
export const fanStatusSchema = z.object({
  online: z.boolean(), on: z.boolean(), simulated: z.boolean(),
  commandId: z.string().max(80).nullable(),
  timestamp: z.string().datetime(), reason: z.string().max(200).optional()
});
export type FanStatus = z.infer<typeof fanStatusSchema>;
export interface Reading {
  _id: string; fridge: FridgeId; temperature: number; humidity: number; receivedAt: string;
}
export interface Setting { _id: FridgeId; threshold: number }
export interface Alert {
  _id: string; fridge: FridgeId; temperature: number; threshold: number;
  recipient: string; createdAt: string; expiresAt: string;
  emailState: 'pending' | 'sent'; lastAttemptAt?: string;
  decision?: 'YES' | 'NO'; decidedAt?: string;
  command?: CommandRecord;
}
export interface Notification {
  _id: string; createdAt: string; kind: 'alert' | 'email' | 'fan' | 'error';
  message: string; fridge?: FridgeId;
  actor?: Actor; commandId?: string; outcome?: string; action?: string;
}
export interface FanCommand { commandId: string; on: boolean; expiresAt: string }
export interface CommandRecord extends FanCommand {
  actor: Actor; createdAt: string; approvalId?: string; fridge?: FridgeId;
  state: 'queued' | 'dispatching' | 'pending' | 'uncertain' | 'confirmed' | 'expired' | 'superseded' | 'cancelled';
}
export interface MailboxCursor { _id: string; uidValidity: string; lastUid: number }
export interface MailboxFailure { _id: string; mailbox: string; uidValidity: string; uid: number; attempts?: number }
export interface Repository {
  initialize(defaultThreshold: number): Promise<void>;
  settings(): Promise<Setting[]>;
  setThreshold(fridge: FridgeId, threshold: number): Promise<void>;
  saveReading(reading: Reading): Promise<boolean>;
  latestReading(fridge: FridgeId): Promise<Reading | null>;
  latestAlert(fridge: FridgeId): Promise<Alert | null>;
  saveAlert(alert: Alert): Promise<void>;
  alert(id: string): Promise<Alert | null>;
  consumeAlert(id: string, decision: 'YES' | 'NO', decidedAt: string, command?: CommandRecord): Promise<boolean>;
  createCommand(command: CommandRecord): Promise<void>;
  updateCommand(command: CommandRecord): Promise<void>;
  outstandingCommands(): Promise<CommandRecord[]>;
  mailboxCursor(id: string): Promise<MailboxCursor | null>;
  saveMailboxCursor(cursor: MailboxCursor): Promise<void>;
  mailboxFailures(mailbox: string, uidValidity: string): Promise<MailboxFailure[]>;
  saveMailboxFailure(failure: MailboxFailure): Promise<void>;
  clearMailboxFailure(id: string): Promise<void>;
  notification(notification: Notification): Promise<void>;
  notifications(): Promise<Notification[]>;
}
export interface Mailer { send(alert: Alert): Promise<void> }
export interface FanPublisher { publish(command: FanCommand): Promise<void> }
export interface MonitorOptions {
  defaultThreshold: number; staleSeconds: number; cooldownSeconds: number;
  replyExpirySeconds: number; emailRetrySeconds: number; recipient: string;
}
