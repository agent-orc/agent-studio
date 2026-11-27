import type { Type } from '@angular/core';
export interface CatalogueState { id: string; inputs: Record<string, unknown>; }
export interface CatalogueEntry { id: string; component: Type<unknown> | null; states: CatalogueState[]; documentation: string; overlay: boolean; }
