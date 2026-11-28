import type { InstructionSource } from './operator-approval.model';

export interface OperatorBatchItem {
  index: number;
  request: { card: string; targetLane: string; reason: string; project: string | null };
  title: string;
  reservation: string | null;
  refusal: string | null;
  state: string;
  resultReason: string | null;
}
export interface OperatorBatch {
  id: string;
  actor: string;
  principalName?: string | null;
  class: string;
  instructionSource: InstructionSource;
  expiresAt: string;
  state: string;
  items: OperatorBatchItem[];
}
