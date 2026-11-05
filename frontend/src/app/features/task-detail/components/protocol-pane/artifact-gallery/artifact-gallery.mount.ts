import {
  ApplicationRef,
  ComponentRef,
  EnvironmentInjector,
  createComponent,
} from '@angular/core';
import type { ConversationEvent } from 'coding-agent-chat/core';
import { ArtifactGalleryComponent } from './artifact-gallery.component';
import {
  artifactBlocks,
  type ConversationArtifact,
  type ConversationArtifactBlock,
} from './artifact-gallery.model';

interface GalleryMount {
  readonly host: HTMLElement;
  readonly ref: ComponentRef<ArtifactGalleryComponent>;
  signature: string;
}

/** Owns the dynamically loaded Angular views that replace library artifact rows. */
export class ArtifactGalleryMountController {
  private readonly galleries = new Map<string, GalleryMount>();
  private readonly pendingIds = new Set<string>();
  private destroyed = false;

  constructor(
    private readonly host: HTMLElement,
    private readonly applicationRef: ApplicationRef,
    private readonly environmentInjector: EnvironmentInjector,
  ) {}

  sync(events: readonly ConversationEvent[]): void {
    this.restoreRows();
    const rows = [...this.host.querySelectorAll<HTMLElement>(
      '[data-testid="conversation-artifact-image"]',
    )];

    const blocks = artifactBlocks(events);
    const activeIds = new Set<string>();
    for (const block of blocks) {
      const blockRows = rows.slice(block.startOrdinal, block.startOrdinal + block.rowCount);
      if (blockRows.length !== block.rowCount || blockRows.length === 0) continue;
      activeIds.add(block.id);
      blockRows.slice(1).forEach((row) => {
        row.hidden = true;
        row.inert = true;
      });
      this.mount(block, blockRows[0]);
    }

    for (const [id, mount] of this.galleries) {
      if (activeIds.has(id)) continue;
      this.destroyMount(mount);
      this.galleries.delete(id);
    }
  }

  destroy(): void {
    this.destroyed = true;
    this.restoreRows();
    for (const mount of this.galleries.values()) this.destroyMount(mount);
    this.galleries.clear();
  }

  private mount(block: ConversationArtifactBlock, firstRow: HTMLElement): void {
    const signature = artifactSignature(block.artifacts);
    let mount = this.galleries.get(block.id);
    if (mount && !mount.host.isConnected) {
      this.destroyMount(mount);
      this.galleries.delete(block.id);
      mount = undefined;
    }
    if (!mount) {
      if (this.pendingIds.has(block.id) || this.destroyed || !firstRow.isConnected) return;
      this.pendingIds.add(block.id);
      const host = firstRow.ownerDocument.createElement('div');
      host.className = 'conv__artifact-gallery';
      host.dataset['testid'] = 'conversation-artifact-gallery-host';
      const ref = createComponent(ArtifactGalleryComponent, {
        environmentInjector: this.environmentInjector,
        hostElement: host,
      });
      this.applicationRef.attachView(ref.hostView);
      mount = { host, ref, signature: '' };
      this.galleries.set(block.id, mount);
      this.pendingIds.delete(block.id);
    }

    firstRow.dataset['artifactGalleryAnchor'] = block.id;
    for (const child of [...firstRow.children]) {
      if (child === mount.host) continue;
      const element = child as HTMLElement;
      element.dataset['artifactGalleryOriginal'] = 'true';
      element.hidden = true;
      element.inert = true;
    }
    firstRow.append(mount.host);
    if (mount.signature !== signature) {
      mount.ref.setInput('artifacts', block.artifacts);
      mount.ref.changeDetectorRef.detectChanges();
      mount.signature = signature;
    }
  }

  private destroyMount(mount: GalleryMount): void {
    this.applicationRef.detachView(mount.ref.hostView);
    mount.ref.destroy();
    mount.host.remove();
  }

  private restoreRows(): void {
    const rows = this.host.querySelectorAll<HTMLElement>(
      '[data-testid="conversation-artifact-image"]',
    );
    rows.forEach((row) => {
      row.hidden = false;
      row.inert = false;
      row.removeAttribute('data-artifact-gallery-anchor');
      row.querySelectorAll<HTMLElement>('[data-artifact-gallery-original="true"]')
        .forEach((element) => {
          element.hidden = false;
          element.inert = false;
          element.removeAttribute('data-artifact-gallery-original');
        });
    });
  }
}

function artifactSignature(artifacts: readonly ConversationArtifact[]): string {
  return artifacts
    .map((artifact) => `${artifact.id}|${artifact.kind}|${artifact.path}|${artifact.url}|${artifact.thumbnailUrl ?? ''}`)
    .join('\n');
}
