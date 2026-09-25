import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import type { GitGraphCommit } from '../../../git';
import { ProjectGitHistoryComponent } from './project-git-history.component';

const commit: GitGraphCommit = {
  sha: 'a'.repeat(40), shortSha: 'aaaaaaa', parentShas: [], authorDateUtc: '2026-09-18T10:00:00Z',
  author: 'Dev', subject: 'A commit', filesChanged: 1, added: 2, removed: 1,
  refs: [], tasks: [], presence: null, deployments: [],
};

describe('ProjectGitHistoryComponent', () => {
  it('scrolls a selected commit into the graph viewport', async () => {
    const scrollIntoView = vi.fn();
    TestBed.configureTestingModule({
      imports: [ProjectGitHistoryComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(ProjectGitHistoryComponent);
    fixture.componentRef.setInput('commits', [commit]);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const row = root.querySelector<HTMLElement>('[data-testid="git-commit-row"]')!;
    Object.defineProperty(row, 'scrollIntoView', { configurable: true, value: scrollIntoView });
    fixture.componentRef.setInput('selectedSha', commit.sha);
    fixture.detectChanges();
    await fixture.whenStable();
    expect(scrollIntoView).toHaveBeenCalledWith({ block: 'nearest' });
  });
});
