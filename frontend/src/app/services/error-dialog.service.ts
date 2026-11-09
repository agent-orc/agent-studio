import { ErrorHandler, Injectable, NgZone, signal, inject } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { ErrorDialogOptions, ErrorDialogState } from '../models/error-dialog.model';

@Injectable({ providedIn: 'root' })
export class ErrorDialogService {
  readonly activeError = signal<ErrorDialogState | null>(null);
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  readonly cliConfigRequest = signal(0);

  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  show(error: unknown, options: ErrorDialogOptions = {}): void {
    const normalizedError = normalizeError(error, options);
    this.activeError.set({
      ...normalizedError,
      output:
        options.output !== undefined ? serializeValue(options.output) : normalizedError.output,
      stackTrace:
        options.stackTrace !== undefined ? options.stackTrace : normalizedError.stackTrace,
      refusal: options.refusal ?? null,
    });
    this.copyState.set('idle');
  }

  close(): void {
    this.activeError.set(null);
    this.copyState.set('idle');
  }

  requestCliConfig(): void {
    this.cliConfigRequest.update((value) => value + 1);
    this.close();
  }

  copyActiveError(): void {
    const activeError = this.activeError();
    if (!activeError) {
      return;
    }

    const payload = [
      `Title: ${activeError.title}`,
      activeError.refusal ? `Reason: ${activeError.refusal.sentence}` : null,
      activeError.refusal ? `Next: ${activeError.refusal.nextAction}` : null,
      activeError.source ? `Source: ${activeError.source}` : null,
      '',
      'Message:',
      activeError.message,
      '',
      'Output:',
      activeError.output,
      '',
      'Stack trace:',
      activeError.stackTrace ?? 'No stack trace available.',
    ]
      .filter((line): line is string => line !== null)
      .join('\n');

    navigator.clipboard.writeText(payload).then(
      () => this.setCopyState('copied'),
      () => this.setCopyState('failed'),
    );
  }

  private setCopyState(state: 'idle' | 'copied' | 'failed'): void {
    this.copyState.set(state);
    if (this.copyResetTimer) {
      clearTimeout(this.copyResetTimer);
    }

    if (state !== 'idle') {
      this.copyResetTimer = setTimeout(() => this.copyState.set('idle'), 2500);
    }
  }
}

@Injectable()
export class ModalErrorHandler implements ErrorHandler {
  private readonly errorDialog = inject(ErrorDialogService);
  private readonly zone = inject(NgZone);

  handleError(error: unknown): void {
    if (isResizeObserverLoopError(error)) {
      return;
    }
    console.error(error);
    this.zone.run(() => {
      this.errorDialog.show(error, {
        title: 'Unexpected application error',
        fallbackMessage: 'The application hit an unexpected error.',
        source: 'Frontend runtime',
      });
    });
  }
}

function isResizeObserverLoopError(error: unknown): boolean {
  const messages: string[] = [];
  if (typeof error === 'string') {
    messages.push(error);
  } else if (error && typeof error === 'object') {
    const record = error as { message?: unknown; cause?: unknown };
    if (typeof record.message === 'string') {
      messages.push(record.message);
    }
    if (record.cause && typeof record.cause === 'object') {
      const causeMessage = (record.cause as { message?: unknown }).message;
      if (typeof causeMessage === 'string') {
        messages.push(causeMessage);
      }
    }
  }
  return messages.some((m) => m.includes('ResizeObserver loop'));
}

function normalizeError(error: unknown, options: ErrorDialogOptions): ErrorDialogState {
  if (error instanceof HttpErrorResponse) {
    return normalizeHttpError(error, options);
  }

  if (error instanceof Error) {
    return {
      title: options.title ?? 'Application error',
      message: error.message || options.fallbackMessage || 'An unexpected error occurred.',
      output: serializeValue({
        name: error.name,
        message: error.message,
      }),
      stackTrace: error.stack ?? null,
      source: options.source ?? null,
      canOpenCliConfig: options.canOpenCliConfig ?? looksLikeCliError(error.message),
    };
  }

  if (typeof error === 'string') {
    return {
      title: options.title ?? 'Application error',
      message: error || options.fallbackMessage || 'An unexpected error occurred.',
      output: error,
      stackTrace: looksLikeStackTrace(error) ? error : null,
      source: options.source ?? null,
      canOpenCliConfig: options.canOpenCliConfig ?? looksLikeCliError(error),
    };
  }

  const objectMessage = extractObjectMessage(error);
  return {
    title: options.title ?? 'Application error',
    message: objectMessage ?? options.fallbackMessage ?? 'An unexpected error occurred.',
    output: serializeValue(error),
    stackTrace: extractStackTrace(error),
    source: options.source ?? null,
    canOpenCliConfig: options.canOpenCliConfig ?? looksLikeCliError(objectMessage),
  };
}

function normalizeHttpError(
  error: HttpErrorResponse,
  options: ErrorDialogOptions,
): ErrorDialogState {
  const payload = error.error;
  const message = extractHttpMessage(error) ?? options.fallbackMessage ?? 'The request failed.';
  const source = options.source ?? buildHttpSource(error);

  return {
    title:
      options.title ??
      (error.status === 0 ? 'Connection error' : `Request failed (${error.status})`),
    message,
    output: serializeValue({
      status: error.status,
      statusText: error.statusText,
      url: error.url,
      message: error.message,
      payload,
    }),
    stackTrace: extractStackTrace(payload) ?? (error as { stack?: string }).stack ?? null,
    source,
    canOpenCliConfig: options.canOpenCliConfig ?? looksLikeCliError(message),
  };
}

function extractHttpMessage(error: HttpErrorResponse): string | null {
  if (error.status === 0) {
    return 'Backend not reachable — is the API running on localhost:5030?';
  }

  if (error.status === 500 && isEmptyHttpPayload(error.error)) {
    return 'Backend returned 500 with an empty body — the API is likely down or crashed mid-request. Run `./api.sh status` and `./api.sh restart` to recover.';
  }

  if (typeof error.error === 'string' && error.error.trim()) {
    return error.error.trim();
  }

  if (typeof error.error === 'object' && error.error !== null) {
    const payload = error.error as Record<string, unknown>;
    const candidates = [payload['error'], payload['message'], payload['detail']];
    for (const candidate of candidates) {
      if (typeof candidate === 'string' && candidate.trim()) {
        return candidate.trim();
      }
    }
  }

  return error.message || null;
}

function isEmptyHttpPayload(payload: unknown): boolean {
  if (payload === null || payload === undefined) return true;
  if (typeof payload === 'string') return payload.trim().length === 0;
  if (typeof payload === 'object')
    return Object.keys(payload as Record<string, unknown>).length === 0;
  return false;
}

function extractObjectMessage(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const record = value as Record<string, unknown>;
  const candidates = [record['message'], record['error'], record['detail']];
  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate.trim()) {
      return candidate.trim();
    }
  }

  return null;
}

function extractStackTrace(value: unknown): string | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const record = value as Record<string, unknown>;
  const candidates = [
    record['stackTrace'],
    record['stack'],
    record['details'],
    record['exception'],
  ];

  for (const candidate of candidates) {
    if (typeof candidate === 'string' && candidate.trim()) {
      return candidate.trim();
    }

    if (candidate && typeof candidate === 'object') {
      const nested = extractStackTrace(candidate);
      if (nested) {
        return nested;
      }
    }
  }

  return null;
}

function serializeValue(value: unknown): string {
  if (typeof value === 'string') {
    return value;
  }

  if (value instanceof Error) {
    return value.stack ?? value.message;
  }

  if (value === undefined) {
    return 'undefined';
  }

  try {
    return JSON.stringify(value, jsonReplacer, 2);
  } catch {
    return String(value);
  }
}

function jsonReplacer(_key: string, value: unknown): unknown {
  if (value instanceof Error) {
    return {
      name: value.name,
      message: value.message,
      stack: value.stack,
    };
  }

  return value;
}

function buildHttpSource(error: HttpErrorResponse): string | null {
  if (!error.url) {
    return 'HTTP request';
  }

  return `HTTP ${error.url}`;
}

function looksLikeCliError(message: string | null | undefined): boolean {
  if (!message) {
    return false;
  }

  return /cli|authenticat/i.test(message);
}

function looksLikeStackTrace(value: string): boolean {
  return /\bat\b/.test(value) || value.includes('\n');
}
