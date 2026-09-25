import { describe, expect, it, beforeEach } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { TagProposalsComponent } from './tag-proposals.component';
import { TagProposalsService } from '../../services/tag-proposals.service';

const proposal = { id: 'p1', projectName: 'Demo', subjectKind: 'task' as const,
  subjectId: 'Demo::one', tagIds: ['decision'], confidence: 0.72, state: 'pending' as const };

describe('tag proposal decisions with frontend mock', () => {
  beforeEach(() => TestBed.configureTestingModule({
    providers: [provideZonelessChangeDetection(), provideHttpClient(), provideHttpClientTesting()],
  }));

  it.each(['accept', 'reject'] as const)('%s resolves only the selected proposal', choice => {
    const service = TestBed.inject(TagProposalsService);
    service.seedMock([proposal, { ...proposal, id: 'p2', subjectId: 'Demo::other' }]);
    const fixture = TestBed.createComponent(TagProposalsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('subjectKind', 'task');
    fixture.componentRef.setInput('subjectId', 'Demo::one');
    fixture.detectChanges();
    const marker = fixture.nativeElement.querySelector('[data-testid="tags-proposed"]') as HTMLElement;
    expect(marker.textContent).toContain('Tags proposed');
    (marker.querySelectorAll('button')[choice === 'accept' ? 0 : 1] as HTMLButtonElement).click();
    expect(service.proposals().find(item => item.id === 'p1')?.state).toBe(choice === 'accept' ? 'accepted' : 'rejected');
    expect(service.proposals().find(item => item.id === 'p2')?.state).toBe('pending');
  });

  it('keeps the classifier marker visible while proposal details are unavailable', () => {
    const fixture = TestBed.createComponent(TagProposalsComponent);
    fixture.componentRef.setInput('projectName', 'Demo');
    fixture.componentRef.setInput('subjectKind', 'wiki');
    fixture.componentRef.setInput('subjectId', 'guide.md');
    fixture.componentRef.setInput('taggingStatus', 'tags-proposed');
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('Awaiting proposal details');
  });
});
