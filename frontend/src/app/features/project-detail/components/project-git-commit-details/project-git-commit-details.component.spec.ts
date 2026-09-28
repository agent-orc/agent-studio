import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Subject } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { ProjectGitService } from '../../../../services/project-git.service';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import type { GitFileChange, GitGraphCommit } from '../../../git';
import { ProjectGitCommitDetailsComponent } from './project-git-commit-details.component';

function graphCommit(sha: string, subject: string): GitGraphCommit {
  return {
    sha,
    shortSha: sha.slice(0, 7),
    parentShas: [],
    authorDateUtc: '2026-09-18T10:00:00Z',
    author: 'Dev',
    subject,
    filesChanged: 1,
    added: 1,
    removed: 0,
    refs: [],
    tasks: [],
    presence: null,
    deployments: [],
  };
}

describe('ProjectGitCommitDetailsComponent', () => {
  it('keeps the selected commit files when an earlier request resolves later', () => {
    const firstSha = 'a'.repeat(40);
    const secondSha = 'b'.repeat(40);
    const requests = new Map<string, Subject<{ sha: string; files: GitFileChange[] }>>();
    const getCommitFiles = vi.fn((_project: string, sha: string) => {
      const request = new Subject<{ sha: string; files: GitFileChange[] }>();
      requests.set(sha, request);
      return request.asObservable();
    });

    TestBed.configureTestingModule({
      imports: [ProjectGitCommitDetailsComponent],
      providers: [
        provideZonelessChangeDetection(),
        { provide: ProjectGitService, useValue: { getCommitFiles } },
        { provide: TaskReferenceNavigationService, useValue: { openTaskKey: vi.fn() } },
      ],
    });
    const fixture = TestBed.createComponent(ProjectGitCommitDetailsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('commit', graphCommit(firstSha, 'First commit'));
    fixture.detectChanges();

    fixture.componentRef.setInput('commit', graphCommit(secondSha, 'Second commit'));
    fixture.detectChanges();
    requests.get(secondSha)!.next({
      sha: secondSha,
      files: [{ status: 'M', path: 'src/second.ts', added: 1, removed: 0 }],
    });
    fixture.detectChanges();

    requests.get(firstSha)!.next({
      sha: firstSha,
      files: [{ status: 'M', path: 'src/first.ts', added: 1, removed: 0 }],
    });
    fixture.detectChanges();

    expect(requests.get(firstSha)!.observed).toBe(false);
    expect(fixture.componentInstance.files().map(file => file.path)).toEqual(['src/second.ts']);
    expect(fixture.nativeElement.textContent).toContain('src/second.ts');
    expect(fixture.nativeElement.textContent).not.toContain('src/first.ts');
  });
});
