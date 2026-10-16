import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { describe, expect, it, vi } from 'vitest';
import { TaskState, type TaskInfo } from '../../../../models/task.model';
import type { TaskScreenshot } from '../../../screenshots';
import { TaskService } from '../../../../services/task.service';
import { OrchestratorPanelStateService } from '../../../orchestrator/state/orchestrator-panel-state.service';
import { StudioPanelStateService } from '../../../studio-shell/services/studio-panel-state.service';
import { ProductFeedbackComponent, productStatus } from './product-feedback.component';

const card = {
  id: 'change-1', taskKey: 'DEM-1', title: 'Make the heading clear',
  projectName: 'Demo', watchPath: '/demo', state: TaskState.HumanReview,
} as TaskInfo;
const shot = {
  url: '/api/tasks/change-1/screenshot?path=results/after.png',
  caption: 'After: home page',
} as TaskScreenshot;

function mount() {
  const tasks = {
    jobs: signal<TaskInfo[]>([card]),
    getJobScreenshots: vi.fn().mockReturnValue(of({ jobId: card.id, screenshots: [shot] })),
    sendPreviewFeedback: vi.fn().mockReturnValue(of({ status: 'queued' })),
    moveJob: vi.fn().mockReturnValue(of({})),
    createJob: vi.fn().mockReturnValue(of({ id: 'change-2' })),
    refresh: vi.fn(),
  };
  const chat = { setOpen: vi.fn() };
  const panels = { visible: signal(true), setVisible: vi.fn() };
  TestBed.configureTestingModule({
    providers: [
      provideZonelessChangeDetection(),
      { provide: TaskService, useValue: tasks },
      { provide: OrchestratorPanelStateService, useValue: chat },
      { provide: StudioPanelStateService, useValue: panels },
    ],
  });
  const fixture = TestBed.createComponent(ProductFeedbackComponent);
  fixture.componentRef.setInput('projectName', 'Demo');
  fixture.componentRef.setInput('watchPath', '/demo');
  fixture.componentRef.setInput('previewAddress', 'https://stage.example.test/');
  fixture.detectChanges();
  return { fixture, tasks, chat, panels };
}

describe('ProductFeedbackComponent', () => {
  it('asks for scope, location, and an example before creating a change', () => {
    const { fixture, tasks } = mount();
    const component = fixture.componentInstance;
    component.startChange();
    component.wish.set('Make the heading clearer');
    component.nextDraft();
    expect(component.draftStep()).toBe('location');
    component.location.set('the home page hero');
    component.nextDraft();
    expect(component.draftStep()).toBe('example');
    component.example.set('Use the style of the pricing page');
    component.nextDraft();
    expect(tasks.createJob).not.toHaveBeenCalled();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="product-proposal"]')?.textContent)
      .toContain('20,000 to 60,000 tokens');
    component.confirmChange();
    expect(tasks.createJob).toHaveBeenCalledOnce();
    expect(tasks.refresh).toHaveBeenCalledWith(true);
    const request = tasks.createJob.mock.calls[0][0];
    expect(request.promptMarkdown).toContain('https://stage.example.test/');
    expect(request.promptMarkdown).toContain('before and after');
  });

  it('offers chat, plain status, and acceptance for a delivery', () => {
    const { fixture, tasks, chat } = mount();
    const root = fixture.nativeElement as HTMLElement;
    root.querySelector<HTMLButtonElement>('[data-testid="product-open-chat"]')!.click();
    expect(chat.setOpen).toHaveBeenCalledWith(true);
    fixture.componentInstance.selectCard(card.id);
    fixture.detectChanges();
    expect(root.querySelector('[data-testid="product-status"]')?.textContent).toContain('Ready to look at');
    expect(root.querySelector('[data-testid="product-markup-image"]')).toBeTruthy();
    root.querySelector<HTMLButtonElement>('[data-testid="product-accept"]')!.click();
    expect(tasks.moveJob).toHaveBeenCalledWith(card.id, TaskState.Completed, card.watchPath);
  });

  it('passes the selected screenshot crop, selector, address, and comment to the same card', async () => {
    const { fixture, tasks } = mount();
    const component = fixture.componentInstance;
    component.selectCard(card.id);
    const olderShot = { ...shot, url: '/api/tasks/change-1/older.png' };
    tasks.getJobScreenshots.mockReturnValue(of({ jobId: card.id, screenshots: [olderShot, shot] }));
    component.refreshScreenshots();
    component.selectShot(olderShot.url);
    component.comment.set('Increase the title contrast.');
    component.selector.set('main h1');
    component.box.set({ x: .1, y: .2, width: .4, height: .3 });
    fixture.detectChanges();
    const image = (fixture.nativeElement as HTMLElement).querySelector<HTMLImageElement>('[data-testid="product-markup-image"]')!;
    Object.defineProperties(image, { naturalWidth: { value: 100 }, naturalHeight: { value: 100 } });
    const drawImage = vi.fn();
    vi.spyOn(HTMLCanvasElement.prototype, 'getContext').mockReturnValue({ drawImage } as unknown as CanvasRenderingContext2D);
    vi.spyOn(HTMLCanvasElement.prototype, 'toBlob').mockImplementation(callback => callback(new Blob(['crop'], { type: 'image/png' })));

    await component.send(image);

    expect(drawImage).toHaveBeenCalledWith(image, 10, 20, 40, 30, 0, 0, 40, 30);
    expect(tasks.sendPreviewFeedback).toHaveBeenCalledOnce();
    const [id, path, data] = tasks.sendPreviewFeedback.mock.calls[0] as [string, string, FormData];
    expect([id, path]).toEqual([card.id, card.watchPath]);
    expect(data.get('comment')).toBe('Increase the title contrast.');
    expect(data.get('selector')).toBe('main h1');
    expect(data.get('previewAddress')).toBe('https://stage.example.test/');
    expect(data.get('crop')).toBeInstanceOf(Blob);
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="product-accept"]')).toBeNull();
    component.refreshScreenshots();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="product-accept"]')).toBeNull();
    tasks.getJobScreenshots.mockReturnValue(of({
      jobId: card.id, screenshots: [olderShot, shot, { ...shot, url: '/api/tasks/change-1/new-after.png' }],
    }));
    component.refreshScreenshots();
    fixture.detectChanges();
    expect((fixture.nativeElement as HTMLElement).querySelector('[data-testid="product-accept"]')).toBeTruthy();
    vi.restoreAllMocks();
  });

  it('uses product words for each active stage', () => {
    expect(productStatus({ ...card, state: TaskState.Progress }).label).toBe('In progress');
    expect(productStatus({ ...card, state: TaskState.AutoReview }).label).toBe('Being checked');
    expect(productStatus(card).label).toBe('Ready to look at');
  });

  it('shows a before and after pair when the delivery provides both captures', () => {
    const { fixture } = mount();
    fixture.componentInstance.selectCard(card.id);
    fixture.componentInstance.screenshots.set([
      { ...shot, url: '/before.png', caption: 'Before: home page' },
      shot,
    ]);
    fixture.detectChanges();
    const comparison = (fixture.nativeElement as HTMLElement).querySelector('[data-testid="product-before-after"]');
    expect(comparison?.textContent).toContain('Before');
    expect(comparison?.textContent).toContain('After');
    expect(comparison?.querySelectorAll('img')).toHaveLength(2);
  });
});
