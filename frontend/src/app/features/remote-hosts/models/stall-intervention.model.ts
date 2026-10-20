/** AGT-3207: the plain-language receipt of one AI Operator stall intervention. */
export interface StallInterventionReceipt {
  id: string;
  subject: string;
  action: string;
  autonomy: 'A0' | 'A1' | 'A2' | string;
  status: 'applied' | 'verified' | 'escalated' | 'proposed' | 'undone' | 'failed' | string;
  observed: string;
  done: string;
  why: string;
  before: string;
  after: string;
  undo: string;
  proposal?: string | null;
  chain: string[];
  undoable: boolean;
  effectEvidence?: string | null;
  outcome?: string | null;
}

export interface StallInterventionRow {
  receipt: StallInterventionReceipt;
  kind: string;
  host: string;
  project: string;
  decidedBy: 'monolith' | 'engine' | string;
  decidedAtUtc: string;
  followUpDueUtc?: string | null;
  affectedCards: string[];
  headline: string;
}

export interface StallInterventionsResponse {
  count: number;
  interventions: StallInterventionRow[];
}
