import { afterEach, describe, expect, it } from 'vitest';
import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { provideZonelessChangeDetection } from '@angular/core';
import { ModalStackService } from '../../../../../services/modal-stack.service';
import { TaskPromptPopoverComponent } from './task-prompt-popover.component';
import { DossierReferenceHydratorService } from '../../../../../services/dossier-reference-hydrator.service';
import {
  DOSSIER_FIXTURE_KEY,
  dossierChipsIn,
  provideDossierCatalogueStub,
} from '../../../../../../testing/dossier-references';

async function mount(markdown: string | null | undefined) {
  await TestBed.configureTestingModule({
    imports: [TaskPromptPopoverComponent],
    providers: [
      provideZonelessChangeDetection(),
      provideHttpClient(),
      provideHttpClientTesting(),
      provideRouter([]),
      provideDossierCatalogueStub(),
    ],
  }).compileComponents();
  const fixture = TestBed.createComponent(TaskPromptPopoverComponent);
  fixture.componentRef.setInput('markdown', markdown);
  fixture.componentRef.setInput('jobId', 'job-1');
  fixture.componentRef.setInput('watchPath', '/tmp/watch');
  fixture.detectChanges();
  return fixture;
}

function el(fixture: { nativeElement: HTMLElement }, testid: string): HTMLElement | null {
  return fixture.nativeElement.querySelector(`[data-testid="${testid}"]`);
}

function overlayEl(testid: string): HTMLElement | null {
  return document.body.querySelector(`[data-testid="${testid}"]`);
}

describe('TaskPromptPopoverComponent', () => {
  afterEach(() => {
    TestBed.resetTestingModule();
    document.body.querySelector('.studio-overlay-root')?.remove();
  });

  it('hides the trigger when there is no prompt text', async () => {
    const fixture = await mount('   ');
    expect(fixture.componentInstance.hasPrompt()).toBe(false);
    expect(el(fixture, 'overview-prompt-trigger')).toBeNull();
  });

  it('renders the trigger when prompt text is present', async () => {
    const fixture = await mount('# Hello\n\nDo the thing.');
    expect(fixture.componentInstance.hasPrompt()).toBe(true);
    expect(el(fixture, 'overview-prompt-trigger')).not.toBeNull();
    // Closed by default — no panel.
    expect(el(fixture, 'overview-prompt-popover')).toBeNull();
  });

  it('opens a centered read-only modal on trigger click and closes on close button', async () => {
    const fixture = await mount('Task body markdown');
    const trigger = el(fixture, 'overview-prompt-trigger') as HTMLButtonElement;

    trigger.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);
    const backdrop = overlayEl('overview-prompt-popover-backdrop');
    expect(backdrop).not.toBeNull();
    expect(backdrop?.classList.contains('prompt-pop__backdrop')).toBe(true);
    const modal = overlayEl('overview-prompt-popover');
    expect(modal).not.toBeNull();
    expect(modal?.classList.contains('prompt-pop__modal')).toBe(true);
    expect(modal?.getAttribute('aria-modal')).toBe('true');
    expect(modal?.style.left).toBe('');
    expect(modal?.style.top).toBe('');
    expect(overlayEl('overview-prompt-popover-body')).not.toBeNull();

    (overlayEl('overview-prompt-popover-close') as HTMLButtonElement).click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
    expect(overlayEl('overview-prompt-popover')).toBeNull();
  });

  it('registers a modal-stack entry only while open (Escape arbitration)', async () => {
    const fixture = await mount('Task body markdown');
    const stack = TestBed.inject(ModalStackService);
    stack.clearForTest();

    fixture.componentInstance.toggle(new MouseEvent('click'));
    fixture.detectChanges();
    expect(stack.topId()).toBe('task-prompt-popover');

    fixture.componentInstance.close();
    fixture.detectChanges();
    expect(stack.topId()).toBeNull();
  });

  it('keeps the modal open when the panel itself is clicked', async () => {
    const fixture = await mount('Task body markdown');
    fixture.componentInstance.toggle(new MouseEvent('click'));
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    overlayEl('overview-prompt-popover')?.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);
  });

  it('closes on backdrop click', async () => {
    const fixture = await mount('Task body markdown');
    fixture.componentInstance.toggle(new MouseEvent('click'));
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(true);

    overlayEl('overview-prompt-popover-backdrop')?.click();
    fixture.detectChanges();
    expect(fixture.componentInstance.open()).toBe(false);
  });

  // AGT-2812: the prompt is one of the document surfaces, so a Dossier named
  // in it is recognisable through the shared chip.
  it('renders a Dossier named in the prompt as a chip', async () => {
    const fixture = await mount(`Continue the work in ${DOSSIER_FIXTURE_KEY}.`);
    (el(fixture, 'overview-prompt-trigger') as HTMLButtonElement).click();
    fixture.detectChanges();
    TestBed.inject(DossierReferenceHydratorService).refresh();

    const body = overlayEl('overview-prompt-popover-body')!;
    const chips = dossierChipsIn(body);
    expect(chips).toHaveLength(1);
    expect(chips[0].textContent).toContain('Decision cards');
  });
});
