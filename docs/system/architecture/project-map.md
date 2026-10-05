# Project map

> Read-only repository inventory captured `2026-07-13T21:53:26.359Z`. Documentation generated `2026-07-13T22:32:53.484Z`.
> Snapshot schema `1`, discovery `project-graph-v1`, documentation `project-map-doc-v1`.
> Snapshot `pg-import-20260713215326359` (previous: `none`); source `snapshot-import`; history `docs/system/architecture/project-map-history/2026-07-13T22-32-53-484Z.json`.

## Scope and interpretation

This map inventories supported solution, project, package, Angular workspace, and GitHub Actions manifest files across every managed project row returned by the registry. Workflow paths prove manifest presence only; they do not assert that a workflow is valid, enabled, or operational.

File and line counts are rough, overlapping component estimates. Generated output, dependency directories, build output, nested repositories, and directory links are excluded. Resolved internal dependencies and unresolved local manifest references are shown separately. This is not a code-call graph, runtime trace, architecture grade, or claim that all projects share one revision.

Canonical project identity is the registry ID (`PROJ-NNN`); short codes and display names are mutable aliases. Technology identity uses the parenthesized canonical slug, such as `dotnet`, `csharp`, and `angular`.

## Portfolio summary

| Project ID | Short code | Project | Discovery | Components | Solutions | Workflow manifests | Technologies | Rough size |
| --- | --- | --- | --- | ---: | ---: | ---: | --- | ---: |
| PROJ-002 | AGT | Agent Task Processor | ready | 8 | 1 | 2 | Angular 21 (angular), ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet), GitHub Actions (github-actions), npm (npm), Playwright (playwright), Roslyn (roslyn), SQLite (sqlite), TypeScript (typescript), Vitest (vitest), xUnit (xunit) | 3,451 files / 611,321 LoC |
| PROJ-011 | CAR | Coding Agent Runner | ready | 4 | 1 | 3 | C# (csharp), .NET 10 (dotnet), GitHub Actions (github-actions), xUnit (xunit) | 175 files / 18,698 LoC |
| PROJ-014 | CAC | Coding Agent Chat | ready | 5 | 1 | 3 | Angular 21 (angular), ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet), GitHub Actions (github-actions), npm (npm), TypeScript (typescript), Vitest (vitest) | 218 files / 45,689 LoC |
| PROJ-015 | TE | Token Economy | ready | 2 | 1 | 3 | C# (csharp), .NET 10 (dotnet), GitHub Actions (github-actions), xUnit (xunit) | 51 files / 4,733 LoC |
| PROJ-012 | WEB | Agent Studio Website | ready | 1 | 0 | 1 | Angular 21 (angular), Express (express), GitHub Actions (github-actions), npm (npm), TypeScript (typescript) | 87 files / 23,399 LoC |

## Source provenance

Each managed repository has independent provenance. A dirty state means the manifest inventory may include tracked or untracked working-tree content beyond the recorded revision.

| Project ID | Short code | Repository | Revision | Working tree | Captured |
| --- | --- | --- | --- | --- | --- |
| PROJ-002 | AGT | PROJ-002 · Agent Task Processor | `36d910d3fc3fb4bc0ef9c8b91c5210f0cbf880f1` | dirty | `2026-07-13T21:53:26.359Z` |
| PROJ-011 | CAR | PROJ-011 · Coding Agent Runner | `dcd51e957c01bc6230014d614427beb7c16189cd` | dirty | `2026-07-13T21:53:26.359Z` |
| PROJ-014 | CAC | PROJ-014 · Coding Agent Chat | `1f63ff349058ccfbbd9963a345b61851876bd7c2` | dirty | `2026-07-13T21:53:26.359Z` |
| PROJ-015 | TE | PROJ-015 · Token Economy | `00fe7f16300996a2808e8a64bed4031832939690` | clean | `2026-07-13T21:53:26.359Z` |
| PROJ-012 | WEB | PROJ-012 · Agent Studio Website | `b97325c49e7070a835ebeefc6307db5dd07f490d` | clean | `2026-07-13T21:53:26.359Z` |

## Components

### AGT: Agent Task Processor

| Component | Kind | Manifest | Technologies | Rough size |
| --- | --- | --- | --- | ---: |
| agent-runner | dotnet | `runner/AgentRunner.csproj` | C# (csharp), .NET 10 (dotnet) | 16 files / 1,943 LoC |
| AgentRunner.Tests | dotnet | `runner.Tests/AgentRunner.Tests.csproj` | C# (csharp), .NET 10 (dotnet), xUnit (xunit) | 6 files / 534 LoC |
| CompanionRelay | dotnet | `companion/relay/CompanionRelay.csproj` | ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet) | 8 files / 304 LoC |
| frontend | npm | `frontend/package.json` | Angular 21 (angular), npm (npm), Playwright (playwright), TypeScript (typescript), Vitest (vitest) | 1,675 files / 287,400 LoC |
| OrchestratorApi | dotnet | `backend/OrchestratorApi.csproj` | ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet), SQLite (sqlite) | 540 files / 145,365 LoC |
| OrchestratorApi.Tests | dotnet | `backend.Tests/OrchestratorApi.Tests.csproj` | C# (csharp), .NET 10 (dotnet), xUnit (xunit) | 429 files / 92,885 LoC |
| SilentCatchAnalyzer | dotnet | `tools/SilentCatchAnalyzer/SilentCatchAnalyzer.csproj` | C# (csharp), .NET (dotnet), Roslyn (roslyn) | 2 files / 72 LoC |
| UpdateService | dotnet | `update-service/UpdateService.csproj` | ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet) | 20 files / 2,206 LoC |

Solutions: `agent-taskboard.sln`

Workflows: `.github/workflows/backend-ci.yml`, `.github/workflows/frontend-lint.yml`

### CAR: Coding Agent Runner

| Component | Kind | Manifest | Technologies | Rough size |
| --- | --- | --- | --- | ---: |
| CodingAgentRunner | dotnet | `src/CodingAgentRunner/CodingAgentRunner.csproj` | C# (csharp), .NET 10 (dotnet) | 70 files / 8,340 LoC |
| CodingAgentRunner.Benchmarks | dotnet | `benchmarks/CodingAgentRunner.Benchmarks/CodingAgentRunner.Benchmarks.csproj` | C# (csharp), .NET 10 (dotnet) | 6 files / 328 LoC |
| CodingAgentRunner.Rendering | dotnet | `src/CodingAgentRunner.Rendering/CodingAgentRunner.Rendering.csproj` | C# (csharp), .NET 10 (dotnet) | 5 files / 402 LoC |
| CodingAgentRunner.Tests | dotnet | `tests/CodingAgentRunner.Tests/CodingAgentRunner.Tests.csproj` | C# (csharp), .NET 10 (dotnet), xUnit (xunit) | 46 files / 4,946 LoC |

Solutions: `CodingAgentRunner.slnx`

Workflows: `.github/workflows/ci.yml`, `.github/workflows/deploy-website.yml`, `.github/workflows/release.yml`

### CAC: Coding Agent Chat

| Component | Kind | Manifest | Technologies | Rough size |
| --- | --- | --- | --- | ---: |
| coding-agent-chat | npm | `package.json` | Angular 21 (angular), npm (npm), TypeScript (typescript), Vitest (vitest) | 218 files / 45,689 LoC |
| coding-agent-chat | npm | `projects/coding-agent-chat/package.json` | Angular 21 (angular), npm (npm) | 126 files / 25,621 LoC |
| CodingAgentChat.Workbench | dotnet | `workbench/CodingAgentChat.Workbench.csproj` | ASP.NET Core (aspnet-core), C# (csharp), .NET 10 (dotnet) | 9 files / 570 LoC |
| conversation-lab | angular-app | `angular.json#conversation-lab` | Angular (angular), TypeScript (typescript) | 21 files / 2,999 LoC |
| website | angular-app | `angular.json#website` | Angular (angular), TypeScript (typescript) | 36 files / 5,301 LoC |

Solutions: `CodingAgentChat.slnx`

Workflows: `.github/workflows/deploy-website.yml`, `.github/workflows/pages.yml`, `.github/workflows/release.yml`

### TE: Token Economy

| Component | Kind | Manifest | Technologies | Rough size |
| --- | --- | --- | --- | ---: |
| TokenEconomy | dotnet | `src/TokenEconomy/TokenEconomy.csproj` | C# (csharp), .NET 10 (dotnet) | 11 files / 1,232 LoC |
| TokenEconomy.Tests | dotnet | `tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj` | C# (csharp), .NET 10 (dotnet), xUnit (xunit) | 7 files / 870 LoC |

Solutions: `TokenEconomy.slnx`

Workflows: `.github/workflows/ci.yml`, `.github/workflows/deploy-website.yml`, `.github/workflows/release.yml`

### WEB: Agent Studio Website

| Component | Kind | Manifest | Technologies | Rough size |
| --- | --- | --- | --- | ---: |
| agent-studio-site-v04 | npm | `04-angular-static-final/package.json` | Angular 21 (angular), Express (express), npm (npm), TypeScript (typescript) | 75 files / 20,730 LoC |

Workflows: `.github/workflows/deploy-website.yml`

## Manifest relations

| Source | Target | Resolution | Kind | Evidence |
| --- | --- | --- | --- | --- |
| AGT / OrchestratorApi | AGT / SilentCatchAnalyzer | resolved | project-reference | `backend/OrchestratorApi.csproj: ../tools/SilentCatchAnalyzer/SilentCatchAnalyzer.csproj` |
| AGT / OrchestratorApi | CAR / CodingAgentRunner | resolved | package | `backend/OrchestratorApi.csproj: CodingAgentRunner [0.7.0]` |
| AGT / agent-runner | CAR / CodingAgentRunner | resolved | package | `runner/AgentRunner.csproj: CodingAgentRunner [0.7.0]` |
| AGT / OrchestratorApi | TE / TokenEconomy | resolved | project-reference | `backend/OrchestratorApi.csproj: ../../../token-economy/src/TokenEconomy/TokenEconomy.csproj` |
| AGT / OrchestratorApi.Tests | AGT / OrchestratorApi | resolved | project-reference | `backend.Tests/OrchestratorApi.Tests.csproj: ../backend/OrchestratorApi.csproj` |
| AGT / OrchestratorApi.Tests | AGT / agent-runner | resolved | project-reference | `backend.Tests/OrchestratorApi.Tests.csproj: ../runner/AgentRunner.csproj` |
| AGT / OrchestratorApi.Tests | AGT / UpdateService | resolved | project-reference | `backend.Tests/OrchestratorApi.Tests.csproj: ../update-service/UpdateService.csproj` |
| AGT / AgentRunner.Tests | AGT / agent-runner | resolved | project-reference | `runner.Tests/AgentRunner.Tests.csproj: ../runner/AgentRunner.csproj` |
| AGT / frontend | CAC / coding-agent-chat | resolved | package | `frontend/package.json: coding-agent-chat file:<local-path>` |
| CAC / CodingAgentChat.Workbench | CAR / CodingAgentRunner | resolved | package | `workbench/CodingAgentChat.Workbench.csproj: CodingAgentRunner` |
| CAR / CodingAgentRunner.Benchmarks | CAR / CodingAgentRunner | resolved | project-reference | `benchmarks/CodingAgentRunner.Benchmarks/CodingAgentRunner.Benchmarks.csproj: ../../src/CodingAgentRunner/CodingAgentRunner.csproj` |
| CAR / CodingAgentRunner.Benchmarks | CAR / CodingAgentRunner.Rendering | resolved | project-reference | `benchmarks/CodingAgentRunner.Benchmarks/CodingAgentRunner.Benchmarks.csproj: ../../src/CodingAgentRunner.Rendering/CodingAgentRunner.Rendering.csproj` |
| CAR / CodingAgentRunner.Rendering | CAR / CodingAgentRunner | resolved | project-reference | `src/CodingAgentRunner.Rendering/CodingAgentRunner.Rendering.csproj: ../CodingAgentRunner/CodingAgentRunner.csproj` |
| CAR / CodingAgentRunner.Tests | CAR / CodingAgentRunner | resolved | project-reference | `tests/CodingAgentRunner.Tests/CodingAgentRunner.Tests.csproj: ../../src/CodingAgentRunner/CodingAgentRunner.csproj` |
| CAR / CodingAgentRunner.Tests | CAR / CodingAgentRunner.Rendering | resolved | project-reference | `tests/CodingAgentRunner.Tests/CodingAgentRunner.Tests.csproj: ../../src/CodingAgentRunner.Rendering/CodingAgentRunner.Rendering.csproj` |
| TE / TokenEconomy.Tests | TE / TokenEconomy | resolved | project-reference | `tests/TokenEconomy.Tests/TokenEconomy.Tests.csproj: ../../src/TokenEconomy/TokenEconomy.csproj` |

## Regeneration

Render the persisted current capture without walking repositories:

```sh
node scripts/generate-project-map.mjs --api http://localhost:5030 --project PROJ-002
```

Create a fresh explicit portfolio capture, persist it as current plus API history, and render that exact snapshot:

```sh
node scripts/generate-project-map.mjs --api http://localhost:5030 --project PROJ-002 --capture
```

For a reviewed or archived API response:

```sh
node scripts/generate-project-map.mjs --snapshot path/to/project-graph.json
```

Each documentation command atomically replaces `docs/system/architecture/project-map.md` and appends a dated JSON provenance envelope under `docs/system/architecture/project-map-history`.
