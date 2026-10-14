import { z } from 'zod';

export const fridgeIds = ['Frig1', 'Frig2'] as const;
export type FridgeId = typeof fridgeIds[number];
export const telemetrySchema = z.object({
  temperature: z.number().finite().min(0).max(50),
  humidity: z.number().finite().min(0).max(100),
  sampleId: z.string().regex(/^[a-zA-Z0-9_-]{1,80}$/).optional()
});
export const thresholdSchema = z.object({ threshold: z.number().finite().min(0).max(50) });
export const fanCommandSchema = z.object({ on: z.boolean() });
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
}
export interface Notification {
  _id: string; createdAt: string; kind: 'alert' | 'email' | 'fan' | 'error';
  message: string; fridge?: FridgeId;
}
export interface FanCommand { commandId: string; on: boolean; expiresAt: string }
export interface Repository {
  initialize(defaultThreshold: number): Promise<void>;
  settings(): Promise<Setting[]>;
  setThreshold(fridge: FridgeId, threshold: number): Promise<void>;
  saveReading(reading: Reading): Promise<boolean>;
  latestReading(fridge: FridgeId): Promise<Reading | null>;
  latestAlert(fridge: FridgeId): Promise<Alert | null>;
  saveAlert(alert: Alert): Promise<void>;
  notification(notification: Notification): Promise<void>;
  notifications(): Promise<Notification[]>;
}
export interface Mailer { send(alert: Alert): Promise<void> }
export interface FanPublisher { publish(command: FanCommand): Promise<void> }
export interface MonitorOptions {
  defaultThreshold: number; staleSeconds: number; cooldownSeconds: number;
  replyExpirySeconds: number; emailRetrySeconds: number; recipient: string;
}
