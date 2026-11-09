export interface ErrorDialogState {
  title: string;
  message: string;
  output: string;
  stackTrace: string | null;
  source: string | null;
  canOpenCliConfig: boolean;
  /** Set for an expected refusal; the dialog leads with its plain wording. */
  refusal?: ErrorDialogRefusal | null;
}

/** AGT-3624: plain wording of an expected refusal. */
export interface ErrorDialogRefusal {
  /** One plain sentence: why the action was refused. */
  sentence: string;
  /** What the operator can do next. */
  nextAction: string;
}

export interface ErrorDialogOptions {
  title?: string;
  fallbackMessage?: string;
  source?: string;
  canOpenCliConfig?: boolean;
  output?: unknown;
  stackTrace?: string | null;
  refusal?: ErrorDialogRefusal | null;
}
