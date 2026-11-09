import { Injectable, signal } from '@angular/core';

/**
 * AGT-3590: below tablet width the studio board shows one container at a
 * time. 720 px is the phone breakpoint the studio shell and header share.
 */
export const BOARD_SINGLE_GROUP_QUERY = '(width < 720px)';

/**
 * Board feature service for per-lane collapse state and container focus.
 * Per-lane collapse remains a customization for individual job columns;
 * top-level Backlog / Active / Done containers do not have their own
 * collapse state.
 */
@Injectable({ providedIn: 'root' })
export class LaneCollapseService {
  readonly collapsedLanes = signal<Set<string>>(
    new Set(safeParseStringArray(localStorage.getItem('collapsedLanes')))
  );

  readonly focusedContainer = signal<string | null>(null);

  /** True below tablet width, where the board shows one container at a time. */
  readonly singleGroupLayout = signal(false);

  /**
   * Container picked in the narrow-board switcher. Kept apart from
   * `focusedContainer` so a phone pick does not leave the wide board focused.
   */
  readonly singleGroupChoice = signal<string | null>(null);

  constructor() {
    const media = typeof matchMedia === 'function' ? matchMedia(BOARD_SINGLE_GROUP_QUERY) : null;
    if (!media) return;
    this.singleGroupLayout.set(media.matches);
    // Root service: the listener lives as long as the application.
    media.addEventListener('change', (event) => this.singleGroupLayout.set(event.matches));
  }

  // ---------- per-lane collapse ----------

  toggleLaneCollapse(state: string): void {
    const current = new Set(this.collapsedLanes());
    if (current.has(state)) current.delete(state);
    else current.add(state);
    this.collapsedLanes.set(current);
    localStorage.setItem('collapsedLanes', JSON.stringify([...current]));
  }

  isLaneCollapsed(state: string): boolean {
    return this.collapsedLanes().has(state);
  }

  /**
   * Flex-grow factor for a lane group. The dashboard distributes its
   * leftover horizontal space across the three groups; if every group
   * grew by the same factor (`flex: 1 1 auto`) groups with fewer
   * expanded lanes ended up with wider lanes than groups with more
   * expanded lanes (e.g. Ready in a 3-lane Backlog became visibly
   * wider than In Progress in a 2-lane Active stretch). Growing each
   * group in proportion to its expanded-lane count makes every
   * expanded lane settle at the same rendered width regardless of
   * which group it lives in. Zero is intentional when every lane in
   * the group is collapsed: the group then sizes to its rails only.
   */
  expandedLaneCount(group: { lanes: { state: string }[] }): number {
    return group.lanes.reduce((n, l) => n + (this.isLaneCollapsed(l.state) ? 0 : 1), 0);
  }

  // ---------- container focus ----------

  isContainerFocused(id: string): boolean {
    return this.focusedContainer() === id;
  }

  /**
   * Focus-expand a container by hiding the other two containers.
   * `allContainerIds` is the full id list from the shell's `laneGroups`
   * (the service stays decoupled from how lanes are grouped).
   */
  toggleContainerFocus(id: string, allContainerIds: string[]): void {
    if (this.focusedContainer() === id) {
      this.clearContainerFocus();
      return;
    }
    if (allContainerIds.includes(id)) {
      this.focusedContainer.set(id);
    }
  }

  clearContainerFocus(): void {
    this.focusedContainer.set(null);
  }

  /**
   * The container the board shows alone, or null when every container shows.
   * Below tablet width one container always shows: the switcher pick, else
   * the wide-board focus, else the first container. A pick whose container
   * is gone (for example an emptied Delivery group) falls back the same way.
   */
  boardFocus(allContainerIds: readonly string[]): string | null {
    if (!this.singleGroupLayout()) return this.focusedContainer();
    const pick = [this.singleGroupChoice(), this.focusedContainer()]
      .find((id) => id !== null && allContainerIds.includes(id));
    return pick ?? allContainerIds[0] ?? null;
  }

  selectSingleGroup(id: string): void {
    this.singleGroupChoice.set(id);
  }
}

function safeParseStringArray(raw: string | null): string[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((s): s is string => typeof s === 'string') : [];
  } catch {
    return [];
  }
}
