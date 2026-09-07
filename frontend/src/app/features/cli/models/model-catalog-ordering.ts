import type { CliModelInfo } from './cli.model';

const OLDER_GENERATION_NOTE = 'Older generation';

interface ParsedModelGeneration {
  family: string;
  generation: readonly number[];
  age: readonly number[];
}

interface RankedModel {
  model: CliModelInfo;
  parsed: ParsedModelGeneration | null;
  index: number;
}

/**
 * Projects a discovery-order catalog into picker order without maintaining a
 * model ranking table. Numeric version segments are compared
 * lexicographically within the id prefix that precedes them:
 *
 * - claude-opus-5 > claude-opus-4-8 > claude-opus-4-7
 * - gpt-5-6 > gpt-5-5
 *
 * An available, non-deprecated model establishes the leading generation for
 * its family. Lower generations remain distinct from backend deprecation: the
 * client projects `olderGeneration` plus a calm explanatory note so picker
 * consumers can group them without changing their availability or lifecycle.
 */
export function orderModelCatalog(models: readonly CliModelInfo[]): readonly CliModelInfo[] {
  const ranked: RankedModel[] = models.map((model, index) => ({
    model,
    parsed: parseModelGeneration(model.id),
    index,
  }));
  const leadingGeneration = new Map<string, readonly number[]>();

  for (const item of ranked) {
    if (item.model.available === false || item.model.deprecated || item.parsed === null) continue;
    const current = leadingGeneration.get(item.parsed.family);
    if (current === undefined || compareGeneration(item.parsed.age, current) > 0) {
      leadingGeneration.set(item.parsed.family, item.parsed.age);
    }
  }

  const projected = ranked.map((item) => {
    const leading = item.parsed === null
      ? undefined
      : leadingGeneration.get(item.parsed.family);
    const superseded = !item.model.deprecated
      && leading !== undefined
      && compareGeneration(item.parsed!.age, leading) < 0;
    const model = superseded
      ? {
          ...item.model,
          olderGeneration: true,
          availabilityNote: item.model.availabilityNote?.trim() || OLDER_GENERATION_NOTE,
        }
      : item.model;

    return { ...item, model };
  });

  return projected
    .sort((left, right) => {
      const availability = Number(left.model.available === false) - Number(right.model.available === false);
      if (availability !== 0) return availability;

      const lifecycle = Number(Boolean(left.model.deprecated || left.model.olderGeneration))
        - Number(Boolean(right.model.deprecated || right.model.olderGeneration));
      if (lifecycle !== 0) return lifecycle;

      if (left.parsed !== null && right.parsed !== null) {
        const generation = compareGeneration(right.parsed.generation, left.parsed.generation);
        if (generation !== 0) return generation;
        const family = left.parsed.family.localeCompare(right.parsed.family);
        if (family !== 0) return family;
      } else if (left.parsed !== null || right.parsed !== null) {
        return left.parsed === null ? 1 : -1;
      }

      return left.index - right.index;
    })
    .map((item) => item.model);
}

function parseModelGeneration(id: string): ParsedModelGeneration | null {
  const normalized = id.trim().toLowerCase().replaceAll('.', '-');
  const claude = normalized.match(/^claude-[a-z]+-(\d+)(?:-(\d+))?/);
  if (claude !== null) {
    const generation = claude.slice(1).filter((part): part is string => part !== undefined).map(Number);
    return {
      family: 'claude',
      generation,
      // Claude's named families share a product generation: Fable 5.1,
      // Opus 5, and Sonnet 5 are peers above every 4.x entry.
      age: generation.slice(0, 1),
    };
  }

  const firstNumber = normalized.search(/\d/);
  if (firstNumber <= 0) return null;

  const family = normalized.slice(0, firstNumber).replace(/-+$/, '');
  const suffix = normalized.slice(firstNumber);
  const match = suffix.match(/^(\d+)(?:-(\d+))?/);
  if (!family || match === null) return null;

  return {
    family,
    generation: match.slice(1).filter((part): part is string => part !== undefined).map(Number),
    age: match.slice(1).filter((part): part is string => part !== undefined).map(Number),
  };
}

function compareGeneration(
  left: readonly number[],
  right: readonly number[],
): number {
  const length = Math.max(left.length, right.length);
  for (let index = 0; index < length; index++) {
    const difference = (left[index] ?? 0) - (right[index] ?? 0);
    if (difference !== 0) return difference;
  }
  return 0;
}
