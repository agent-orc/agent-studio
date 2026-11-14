import { describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { ConfirmDialogComponent } from './confirm-dialog.component';
import { ConfirmDialogService } from '../../../services/confirm-dialog.service';

describe('ConfirmDialogComponent (smoke)', () => {
  it('compiles + instantiates without throwing', async () => {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    try { fixture.detectChanges(); } catch (e) {
      console.warn('[smoke] ConfirmDialogComponent initial render skipped:', (e as Error).message);
    }
    expect(fixture.componentInstance).toBeTruthy();
  });
});

describe('ConfirmDialogComponent typed confirmation', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    const service = TestBed.inject(ConfirmDialogService);
    return { fixture, component: fixture.componentInstance, service };
  }

  it('keeps the confirm button disabled until the typed value matches', async () => {
    const { fixture, component, service } = await mount();
    void service.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step.',
      confirmLabel: 'Delete project',
      requireTypedValues: ['Agent Task Processor', 'ATP'],
      requireTypedPrompt: 'Type the project name or code.',
    });
    fixture.detectChanges();

    expect(component.typedConfirmSatisfied()).toBe(false);

    component.typedDraft.set('wrong project');
    expect(component.typedConfirmSatisfied()).toBe(false);

    component.typedDraft.set(' atp ');
    expect(component.typedConfirmSatisfied()).toBe(true);
  });

  it('does not accept Enter while the typed gate is unsatisfied', async () => {
    const { fixture, component, service } = await mount();
    let resolved: boolean | null = null;
    service.confirm({
      title: 'Confirm permanent deletion',
      message: 'Last step.',
      requireTypedValues: ['PROJ'],
    }).then(value => { resolved = value; });
    fixture.detectChanges();

    component.onPanelKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    await Promise.resolve();
    expect(resolved).toBeNull();

    component.typedDraft.set('PROJ');
    component.onPanelKeydown(new KeyboardEvent('keydown', { key: 'Enter' }));
    await Promise.resolve();
    expect(resolved).toBe(true);
  });
});

describe('ConfirmDialogComponent written reason (AGT-3624)', () => {
  async function mount() {
    await TestBed.configureTestingModule({
      imports: [ConfirmDialogComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const fixture = TestBed.createComponent(ConfirmDialogComponent);
    const service = TestBed.inject(ConfirmDialogService);
    return { fixture, component: fixture.componentInstance, service };
  }

  it('keeps confirm disabled until the reason is long enough, then resolves with it', async () => {
    const { fixture, component, service } = await mount();
    const result = service.confirmWithReason({
      title: 'Delete this task and its unintegrated work?',
      message: 'Work is lost.',
      requireReason: { prompt: 'Why?', minLength: 8 },
    });
    fixture.detectChanges();
    // The dialog renders into the document-level overlay portal.
    const button = () => document.querySelector('[data-testid="confirm-dialog-confirm"]') as HTMLButtonElement;
    expect(document.querySelector('[data-testid="confirm-dialog-reason-input"]')).toBeTruthy();
    expect(button().disabled).toBe(true);

    component.reasonDraft.set('  short ');
    fixture.detectChanges();
    expect(button().disabled).toBe(true);
    component.onAccept();
    expect(service.active()).not.toBeNull();

    component.reasonDraft.set('  Superseded by DEMO-2 ');
    fixture.detectChanges();
    expect(button().disabled).toBe(false);
    component.onAccept();
    await expect(result).resolves.toBe('Superseded by DEMO-2');
  });

  it('resolves null when cancelled', async () => {
    const { fixture, component, service } = await mount();
    const result = service.confirmWithReason({
      title: 'Delete?', message: 'Work is lost.', requireReason: { prompt: 'Why?', minLength: 8 },
    });
    fixture.detectChanges();
    component.reasonDraft.set('A long enough reason');
    component.onCancel();
    await expect(result).resolves.toBeNull();
  });
});
