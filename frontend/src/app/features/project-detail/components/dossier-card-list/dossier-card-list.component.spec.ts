import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it, vi } from 'vitest';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { DossierCardListComponent } from './dossier-card-list.component';

describe('DossierCardListComponent', () => {
  it('shows lane, state, environments and slice progress and opens a card', async () => {
    const openReferenceOrNotify = vi.fn();
    await TestBed.configureTestingModule({
      imports: [DossierCardListComponent],
      providers: [provideZonelessChangeDetection(),
        { provide: TaskReferenceNavigationService, useValue: { openReferenceOrNotify } }],
    }).compileComponents();
    const fixture = TestBed.createComponent(DossierCardListComponent);
    fixture.componentRef.setInput('list', {
      projectName: 'Demo', dossierKey: 'DEM-W1', dossierPath: 'docs/operations/one/index.html',
      implementationSlices: 2, completedSlices: 1,
      cards: [{ key: 'DEM-1', id: 'one', title: 'Ship API', lane: '6-completed',
        state: '6-completed', environments: ['staging'], section: '§4',
        decision: 'NV-D2', implementationIndex: 0 }],
    });
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('[data-testid="dossier-slice-progress"]')?.textContent).toContain('1 / 2');
    expect(root.textContent).toContain('Environments: staging');
    expect(root.textContent).toContain('Section: §4');
    expect(root.textContent).toContain('Decision: NV-D2');
    root.querySelector<HTMLButtonElement>('[data-testid="dossier-card-DEM-1"]')?.click();
    expect(openReferenceOrNotify).toHaveBeenCalledWith('DEM-1');
  });
});
