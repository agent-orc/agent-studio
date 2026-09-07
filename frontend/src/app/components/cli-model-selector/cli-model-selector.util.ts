import type { CliModelInfo } from '../../features/cli';

export function normalizeThinkingLevel(
  models: readonly CliModelInfo[],
  modelId: string,
  requested: string | null,
): string | null {
  if (!modelId) return null;
  const model = models.find((candidate) => candidate.id === modelId);
  const levels = model?.thinkingLevels ?? [];
  if (levels.length === 0) return null;
  if (requested && levels.includes(requested)) return requested;
  return model?.defaultThinkingLevel ?? levels[0] ?? null;
}

export function modelAvailabilityNote(model: CliModelInfo): string | null {
  if (model.available !== false) return null;
  return model.availabilityNote?.trim() || 'Not offered by the installed CLI.';
}

export function modelAriaLabel(model: CliModelInfo): string {
  const note = modelAvailabilityNote(model);
  return note ? `${model.label || model.id}. Unavailable. ${note}` : model.label || model.id;
}

export function olderModelNote(model: CliModelInfo): string {
  return model.availabilityNote?.trim() || 'Older generation';
}

export function olderModelAriaLabel(model: CliModelInfo): string {
  const unavailable = model.available === false ? ' Unavailable.' : '';
  return `${model.label || model.id}. Older generation.${unavailable} ${olderModelNote(model)}`;
}

export function moveRadioSelection<T>(
  event: KeyboardEvent,
  items: readonly T[],
  current: T,
  commit: (next: T) => void,
): void {
  if (items.length === 0) return;
  const forward = event.key === 'ArrowRight' || event.key === 'ArrowDown';
  const backward = event.key === 'ArrowLeft' || event.key === 'ArrowUp';
  const home = event.key === 'Home';
  const end = event.key === 'End';
  if (!forward && !backward && !home && !end) return;
  event.preventDefault();
  event.stopPropagation();
  const currentIndex = Math.max(0, items.findIndex((item) => item === current));
  let nextIndex = currentIndex;
  if (forward) nextIndex = (currentIndex + 1) % items.length;
  if (backward) nextIndex = (currentIndex - 1 + items.length) % items.length;
  if (home) nextIndex = 0;
  if (end) nextIndex = items.length - 1;
  commit(items[nextIndex]);
}
