export interface SubscriptionUsageTotal {
  name: string;
  tokens: number;
  receipts: number;
}

export interface SubscriptionUsageReceipt {
  project: string | null;
  card: string | null;
  at: string;
  kind: string;
  model: string;
  cli: string;
  tokens: number;
  source: string;
}

export interface SubscriptionWeek {
  cli: string;
  subscriptionKey: string | null;
  startUtc: string | null;
  resetAtUtc: string | null;
  usedPct: number | null;
  observedAt: string | null;
  totalTokens: number;
  receiptCount: number;
  cards: SubscriptionUsageTotal[];
  kinds: SubscriptionUsageTotal[];
  models: SubscriptionUsageTotal[];
  receipts: SubscriptionUsageReceipt[];
  notice: string | null;
}
