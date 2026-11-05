/** Contain viewport keys in the focused fallback table. Narrow tables scroll
 * horizontally; wider tables use their vertical scroll owner if one exists. */
export function scrollCliModelsTable(event: KeyboardEvent): void {
  const keys = ['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight', 'PageUp', 'PageDown', 'Home', 'End'];
  if (!keys.includes(event.key) || event.target !== event.currentTarget) return;
  const surface = event.currentTarget as HTMLElement;
  const vertical = surface.scrollHeight > surface.clientHeight;
  const horizontal = surface.scrollWidth > surface.clientWidth;
  if (!vertical && !horizontal) return;
  const current = vertical ? surface.scrollTop : surface.scrollLeft;
  const maximum = vertical
    ? surface.scrollHeight - surface.clientHeight
    : surface.scrollWidth - surface.clientWidth;
  const page = vertical ? surface.clientHeight : surface.clientWidth;
  const delta = event.key === 'ArrowUp' || event.key === 'ArrowLeft' ? -48
    : event.key === 'ArrowDown' || event.key === 'ArrowRight' ? 48
    : event.key === 'PageUp' ? -page : event.key === 'PageDown' ? page : 0;
  const next = event.key === 'Home' ? 0 : event.key === 'End' ? maximum
    : Math.max(0, Math.min(maximum, current + delta));
  if (vertical) surface.scrollTop = next;
  else surface.scrollLeft = next;
  event.preventDefault();
  event.stopPropagation();
}
