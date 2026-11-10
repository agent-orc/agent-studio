export interface ErrorDialogState {
  title: string;
  message: string;
  output: string;
  stackTrace: string | null;
  source: string | null;
  canOpenCliConfig: boolean;
}

export interface ErrorDialogOptions {
  title?: string;
  fallbackMessage?: string;
  source?: string;
  canOpenCliConfig?: boolean;
  output?: unknown;
  stackTrace?: string | null;
  /** Action to refocus after the dialog closes, including when Escape closes it. */
  returnFocusSelector?: string;
}
