import { TestBed, type ComponentFixture } from '@angular/core/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { Subject, startWith, type Observable } from 'rxjs';
import { describe, expect, it } from 'vitest';
import { ProjectInstanceDeploySectionComponent } from './project-instance-deploy-section';
import { InstanceDeployDataService } from '../../services/instance-deploy-data.service';
import { INSTANCE_FIXTURE_NOW, instanceDeployFixture } from '../../testing/instance-deploy-fixtures';
import type { DeployPanel } from '../../state/deploy-area.model';
import type { InstanceDeployView } from '../../state/instance-deploy.model';

class FakeInstanceData {
  readonly reads: string[] = [];
  readonly answers = new Subject<DeployPanel<InstanceDeployView>>();
  instance(project: string): Observable<DeployPanel<InstanceDeployView>> {
    this.reads.push(project);
    return this.answers.pipe(startWith<DeployPanel<InstanceDeployView>>({ status: 'loading' }));
  }
}

async function render(data: FakeInstanceData): Promise<ComponentFixture<ProjectInstanceDeploySectionComponent>> {
  await TestBed.configureTestingModule({
    imports: [ProjectInstanceDeploySectionComponent],
    providers: [provideZonelessChangeDetection(), { provide: InstanceDeployDataService, useValue: data }],
  }).compileComponents();
  const fixture = TestBed.createComponent(ProjectInstanceDeploySectionComponent);
  fixture.componentRef.setInput('project', 'Agent Studio');
  fixture.componentRef.setInput('now', INSTANCE_FIXTURE_NOW);
  await settle(fixture);
  return fixture;
}

async function settle(fixture: ComponentFixture<unknown>): Promise<void> {
  fixture.detectChanges();
  await fixture.whenStable();
  fixture.detectChanges();
}

const find = (fixture: ComponentFixture<unknown>, id: string) =>
  (fixture.nativeElement as HTMLElement).querySelector(`[data-testid="${id}"]`);

describe('ProjectInstanceDeploySectionComponent', () => {
  it('shows the running Studio instance once its source project answers', async () => {
    const data = new FakeInstanceData();
    const fixture = await render(data);
    expect(data.reads).toEqual(['Agent Studio']);
    expect(find(fixture, 'instance-deploy')).toBeNull();
    data.answers.next({ status: 'ready', value: instanceDeployFixture() });
    await settle(fixture);
    expect(find(fixture, 'instance-deploy')?.textContent).toContain('Production · this Studio');
    expect(find(fixture, 'instance-deploy-pending')?.textContent).toContain('AGT-3879');
  });

  it('renders nothing for a project that is not the instance source', async () => {
    const data = new FakeInstanceData();
    const fixture = await render(data);
    data.answers.next({ status: 'unavailable' });
    await settle(fixture);
    expect((fixture.nativeElement as HTMLElement).textContent?.trim()).toBe('');
  });

  it('names a failed read instead of hiding it', async () => {
    const data = new FakeInstanceData();
    const fixture = await render(data);
    data.answers.next({ status: 'error', message: 'Deploy records could not be read.' });
    await settle(fixture);
    expect(find(fixture, 'instance-deploy-notice')?.textContent).toContain('This Studio: Deploy records could not be read.');
  });

  it('re-reads on each refresh tick and keeps the shown view meanwhile, but not across projects', async () => {
    const data = new FakeInstanceData();
    const fixture = await render(data);
    data.answers.next({ status: 'ready', value: instanceDeployFixture() });
    await settle(fixture);
    fixture.componentRef.setInput('now', INSTANCE_FIXTURE_NOW + 15_000);
    await settle(fixture);
    expect(data.reads).toEqual(['Agent Studio', 'Agent Studio']);
    expect(find(fixture, 'instance-deploy')).not.toBeNull();
    fixture.componentRef.setInput('project', 'Website');
    await settle(fixture);
    expect(data.reads).toEqual(['Agent Studio', 'Agent Studio', 'Website']);
    expect(find(fixture, 'instance-deploy')).toBeNull();
  });
});
