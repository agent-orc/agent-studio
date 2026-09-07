import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { beforeEach, describe, expect, it } from 'vitest';
import { OrchestratorSideSheetComponent } from './orchestrator-side-sheet.component';

const AUTOMATIC_CHIP = '[data-testid="chat-context-attachment-context:automatic"]';
const PAGE_SOURCE_ID = 'page:demo-project:page:demo-project/concepts/context.md';

/**
 * The composer's context contract after the coding-agent-chat 0.4.1 adoption:
 * chips (when present), textarea, footer. No host-owned picker row, no library
 * toolbar row, no breadcrumb, no image upload.
 */
describe('OrchestratorSideSheetComponent composer context chips', () => {
  beforeEach(() => sessionStorage.removeItem('atp.studio.orchestratorOpen.v1'));

  async function makeFixture() {
    await TestBed.configureTestingModule({
      imports: [OrchestratorSideSheetComponent],
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();
    const fixture = TestBed.createComponent(OrchestratorSideSheetComponent);
    fixture.componentRef.setInput('projects', ['demo-project']);
    fixture.componentRef.setInput('composerContext', { project: 'demo-project', surface: 'Board' });
    fixture.componentInstance.activeProject.set('demo-project');
    fixture.detectChanges();
    return fixture;
  }

  function withPageContext(fixture: Awaited<ReturnType<typeof makeFixture>>) {
    fixture.componentRef.setInput('pageContext', {
      projectName: 'demo-project',
      relPath: 'concepts/context.md',
      title: 'Context model',
      pageType: 'concept',
      excerpt: 'Conversation scope and source receipts.',
    });
    fixture.detectChanges();
  }

  it('renders the automatic current-tab chip with its estimate and no host picker row', async () => {
    const fixture = await makeFixture();
    const root = fixture.nativeElement as HTMLElement;

    const automatic = root.querySelector(AUTOMATIC_CHIP);
    expect(automatic).not.toBeNull();
    expect(automatic?.textContent).toContain('Board');
    expect(automatic?.textContent).toContain('~1.6k');
    expect(root.querySelector('[data-testid="orch-context-draft"]')).toBeNull();
  });

  it('keeps the composer at chips, textarea and footer - no toolbar row, breadcrumb or attach', async () => {
    const fixture = await makeFixture();
    const root = fixture.nativeElement as HTMLElement;

    expect(root.querySelectorAll('[data-testid="chat-composer-foot"]')).toHaveLength(1);
    expect(root.querySelector('[data-testid="chat-input"]')).not.toBeNull();
    expect(root.querySelector('[data-testid="chat-toolbar"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-composer-context"]')).toBeNull();
    expect(root.querySelector('[data-testid="chat-attach"]')).toBeNull();
  });

  it('drops the orchestrator wording from the placeholder but keeps the /bug hint', async () => {
    const fixture = await makeFixture();
    const input = (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLTextAreaElement>('[data-testid="chat-input"]')!;

    expect(input.placeholder).not.toMatch(/orchestrator/i);
    expect(input.placeholder).toContain('/bug');
  });

  it('adds a source through the picker popover and removes it from the chip row', async () => {
    const fixture = await makeFixture();
    withPageContext(fixture);
    const root = fixture.nativeElement as HTMLElement;

    // The library's `+` affordance is the only entry point into the picker.
    root.querySelector<HTMLButtonElement>('[data-testid="chat-context-attachment-add"]')!.click();
    fixture.detectChanges();
    root.querySelector<HTMLButtonElement>('[data-testid="orch-context-current-source"]')!.click();
    fixture.detectChanges();

    const chip = root.querySelector(`[data-testid="chat-context-attachment-${PAGE_SOURCE_ID}"]`);
    expect(chip).not.toBeNull();
    expect(chip?.textContent).toContain('Context model');
    expect(chip?.textContent).toContain('~1.2k');

    root
      .querySelector<HTMLButtonElement>(`[data-testid="chat-context-attachment-remove-${PAGE_SOURCE_ID}"]`)!
      .click();
    fixture.detectChanges();

    expect(root.querySelector(`[data-testid="chat-context-attachment-${PAGE_SOURCE_ID}"]`)).toBeNull();
    expect(fixture.componentInstance.contextAttachments()).toEqual([]);
    expect(root.querySelector(AUTOMATIC_CHIP)).not.toBeNull();
  });

  it('removing the automatic chip excludes the current tab from the next project message', async () => {
    const fixture = await makeFixture();
    const root = fixture.nativeElement as HTMLElement;

    root
      .querySelector<HTMLButtonElement>('[data-testid="chat-context-attachment-remove-context:automatic"]')!
      .click();
    fixture.detectChanges();

    expect(fixture.componentInstance.contextDismissed()).toBe(true);
    expect(root.querySelector(AUTOMATIC_CHIP)).toBeNull();
    // With no chips left the library moves the add affordance into the footer.
    expect(root.querySelector('[data-testid="chat-context-attachment-add"]')).not.toBeNull();
  });
});
