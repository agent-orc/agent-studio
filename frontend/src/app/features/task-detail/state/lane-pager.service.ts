import { Injectable, computed, signal } from '@angular/core';
import { TaskInfo } from '../../../models/task.model';
import { LANE_PRESENTATIONS } from '../../../models/lane-presentation';
import { taskUrlKey } from './task-url';

/**
 * Lane pager snapshot: the captured iteration the detail header walks
 * with its Prev / N of M / Next controls. The snapshot is intentionally
 * stable across status changes: if the user moves the current job out
 * of the captured lane mid-iteration, the snapshot keeps the moved job
 * at its original index so a subsequent "Next" click advances to the
 * job that was after it at capture time, not to whatever live ordering
 * shows now.
 *
 * Persistence: stored in sessionStorage under a versioned key so a
 * page reload restores the iteration. The snapshot is tab-scoped on
 * purpose; opening a second tab on the same workspace starts its own
 * iteration.
 */
export interface LanePagerEntry {
  taskKey: string;
  /** Globally stable AGT-NNN reference used by the browser URL. */
  routeKey: string | null;
  id: string;
  watchPath: string;
  title: string | null;
}

export interface LanePagerSnapshot {
  lane: string;
  jobs: LanePagerEntry[];
  index: number;
  capturedAt: number;
}

const STORAGE_KEY = 'app:lanePager:v2';

/**
 * Lane display names, keyed by lane state.
 *
 * AGT-2715: this map used to be one of five hand-maintained copies. It now
 * projects the lane presentation catalogue, so it cannot drift from the board
 * header, the Result view, or the settings list. Prefer importing `laneName`
 * directly in new code; this export stays for the existing call sites that
 * want a plain lookup record.
 */
export const LANE_LABELS: Record<string, string> = Object.fromEntries(
  LANE_PRESENTATIONS.map((lane) => [lane.state, lane.name]),
);

@Injectable({ providedIn: 'root' })
export class LanePagerService {
  /**
   * The captured iteration. `null` when the detail view wasn't opened
   * from a lane (e.g. pasted URL with no prior snapshot) or after the
   * snapshot was cleared.
   */
  readonly snapshot = signal<LanePagerSnapshot | null>(this.loadFromStorage());

  /** 1-based human-readable position; 0 when no snapshot. */
  readonly position = computed(() => {
    const s = this.snapshot();
    return s ? s.index + 1 : 0;
  });

  readonly total = computed(() => this.snapshot()?.jobs.length ?? 0);

  readonly canPrev = computed(() => {
    const s = this.snapshot();
    return !!s && s.index > 0;
  });

  readonly canNext = computed(() => {
    const s = this.snapshot();
    return !!s && s.index < s.jobs.length - 1;
  });

  readonly laneLabel = computed(() => {
    const s = this.snapshot();
    if (!s) return '';
    return LANE_LABELS[s.lane] ?? s.lane;
  });

  /**
   * Capture a fresh snapshot of `lane`'s current peers, anchoring the
   * iteration index at `currentJobKey`. Pass an empty `peers` list (or
   * a job not in the lane) to clear the snapshot.
   */
  capture(lane: string, peers: readonly TaskInfo[], currentJobKey: string): void {
    if (peers.length === 0) {
      this.clear();
      return;
    }
    const jobs: LanePagerEntry[] = peers.map(p => ({
      taskKey: p.taskKey,
      routeKey: taskUrlKey(p),
      id: p.id,
      watchPath: p.watchPath,
      title: p.title ?? null,
    }));
    const idx = jobs.findIndex(j => j.taskKey === currentJobKey);
    if (idx < 0) {
      this.clear();
      return;
    }
    const next: LanePagerSnapshot = {
      lane,
      jobs,
      index: idx,
      capturedAt: Date.now(),
    };
    this.snapshot.set(next);
    this.persist(next);
  }

  /**
   * Advance the snapshot index by `delta` (`+1` for Next, `-1` for
   * Prev). Returns the entry at the new index, or `null` if the move
   * would step past a boundary or no snapshot exists.
   */
  step(delta: -1 | 1): LanePagerEntry | null {
    const s = this.snapshot();
    if (!s) return null;
    const nextIdx = s.index + delta;
    if (nextIdx < 0 || nextIdx >= s.jobs.length) return null;
    const updated: LanePagerSnapshot = { ...s, index: nextIdx };
    this.snapshot.set(updated);
    this.persist(updated);
    return s.jobs[nextIdx];
  }

  /**
   * Drop `taskKey` from the snapshot and yield the entry that now sits
   * at its slot. Use after a user-initiated mutation removes the
   * currently visible job from the iteration (delete from detail,
   * lane change via state dropdown, triage move/delete) so the pager
   * auto-advances to the next item the user wanted to triage.
   *
   * The numeric index is preserved across the removal: dropping the
   * entry at index `i` shifts the entry that was at `i + 1` down to
   * `i`, which is the "k+1" the user asked for. When the dropped
   * entry was the last in the list, the new index clamps to
   * `length - 1` so Prev/Next stay valid. Returns `null` (and clears
   * the snapshot) when the lane is now empty, or when `taskKey` was
   * not part of the snapshot.
   */
  removeAndAdvance(taskKey: string): LanePagerEntry | null {
    const s = this.snapshot();
    if (!s) return null;
    const idx = s.jobs.findIndex(j => j.taskKey === taskKey);
    if (idx < 0) return null;
    const newJobs = [...s.jobs.slice(0, idx), ...s.jobs.slice(idx + 1)];
    if (newJobs.length === 0) {
      this.clear();
      return null;
    }
    const newIdx = Math.min(idx, newJobs.length - 1);
    const updated: LanePagerSnapshot = { ...s, jobs: newJobs, index: newIdx };
    this.snapshot.set(updated);
    this.persist(updated);
    return newJobs[newIdx];
  }

  /**
   * Shrink the snapshot by removing `taskKey` without navigating.
   * Unlike `removeAndAdvance`, the view stays on the current job even
   * though it is no longer part of the captured iteration (e.g. an
   * external lane change while the user is reading). The snapshot's
   * `index` clamps to `length - 1` so a subsequent Prev/Next step
   * lands on a valid peer. Clears the snapshot when it becomes empty.
   */
  dropFromSnapshot(taskKey: string): void {
    const s = this.snapshot();
    if (!s) return;
    const idx = s.jobs.findIndex(j => j.taskKey === taskKey);
    if (idx < 0) return;
    const newJobs = [...s.jobs.slice(0, idx), ...s.jobs.slice(idx + 1)];
    if (newJobs.length === 0) {
      this.clear();
      return;
    }
    const newIdx = Math.min(s.index, newJobs.length - 1);
    const updated: LanePagerSnapshot = { ...s, jobs: newJobs, index: newIdx };
    this.snapshot.set(updated);
    this.persist(updated);
  }

  /**
   * Reconcile the snapshot's index to a specific job key. Used when
   * selection lands on a snapshot member through a path other than
   * pager step (e.g. URL restore on reload). No-op when the job is
   * not part of the current snapshot.
   */
  reanchorTo(taskKey: string): void {
    const s = this.snapshot();
    if (!s) return;
    const idx = s.jobs.findIndex(j => j.taskKey === taskKey);
    if (idx < 0 || idx === s.index) return;
    const updated: LanePagerSnapshot = { ...s, index: idx };
    this.snapshot.set(updated);
    this.persist(updated);
  }

  clear(): void {
    this.snapshot.set(null);
    if (typeof sessionStorage !== 'undefined') {
      try { sessionStorage.removeItem(STORAGE_KEY); } catch { /* ignore quota errors */ }
    }
  }

  private persist(s: LanePagerSnapshot): void {
    if (typeof sessionStorage === 'undefined') return;
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s));
    } catch {
      /* sessionStorage may be unavailable in private mode; iteration still works in-memory */
    }
  }

  private loadFromStorage(): LanePagerSnapshot | null {
    if (typeof sessionStorage === 'undefined') return null;
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as LanePagerSnapshot;
      if (
        !parsed ||
        typeof parsed.lane !== 'string' ||
        !Array.isArray(parsed.jobs) ||
        typeof parsed.index !== 'number'
      ) return null;
      return parsed;
    } catch {
      return null;
    }
  }
}
