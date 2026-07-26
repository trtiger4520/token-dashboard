# Repository Guidelines

## Project Structure & Module Organization

The solution is split by responsibility:

- `src/TokenDashboard.Core/` contains domain contracts, pricing rules, token types, sessions, and turns
- `src/TokenDashboard.Data/` owns SQLite schema migrations, adapters, imports, deduplication, pricing history, and FTS
- `src/TokenDashboard.Api/` provides the localhost Minimal API, security middleware, analytics, exports, and SPA hosting
- `src/TokenDashboard.Web/` contains the Vue 3 and TypeScript application; follow `DESIGN.md` for all UI work
- `tests/*.Tests/` mirrors the .NET projects, while Vue tests live beside components as `*.spec.ts`
- `tests/fixtures/public/` contains synthetic fixtures; never commit data under `tests/fixtures/private/`
- `scripts/` contains cross-platform build, publish, and smoke-test entry points

## Build, Test, and Development Commands

Use the versions pinned in `global.json` and `package.json`.

```powershell
pnpm --dir src/TokenDashboard.Web install --lockfile=false
pnpm --dir src/TokenDashboard.Web dev
dotnet run --project src/TokenDashboard.Api/TokenDashboard.Api.csproj
```

Run `scripts/build.ps1` on Windows or `bash scripts/build.sh` on macOS/Linux for restore, frontend build, .NET build, and all tests. Run `scripts/publish.ps1` or `bash scripts/publish.sh` to produce framework-dependent artifacts under `artifacts/publish/`. Use `scripts/smoke.ps1` to validate the Windows published executable.

## Coding Style & Naming Conventions

`.editorconfig` is authoritative: four-space indentation for C# and two spaces for Markdown, JSON, and YAML. C# uses file-scoped namespaces, braces, nullable reference types, analyzers, and warnings as errors. Use PascalCase for public C# members, camelCase for locals, and descriptive `Async` suffixes where applicable. Vue code must remain TypeScript-strict, accessible, responsive, and consistent with the Blueprint Margin design tokens.

## Testing Guidelines

.NET tests use xUnit; Vue tests use Vitest and Vue Test Utils. Name tests after observable behavior, for example `StatisticsCountEachTurnOnceWhileSessionDetailKeepsAllSubEvents`. Every bug fix requires a regression test. There is no numeric coverage gate, but new adapters, pricing logic, migrations, API contracts, and UI states must be exercised.

## Commit & Pull Request Guidelines

The repository has no established commit history. Use concise imperative subjects such as `Fix turn token aggregation`, keeping each commit focused. Pull requests should explain behavior changes, list verification commands, link relevant issues, call out schema or security effects, and include desktop/mobile screenshots for UI changes. Never include `Co-Authored-By` trailers.

## Security & Local Data

Keep the API loopback-only and preserve fragment-based session-key delivery. Never log or persist keys, raw source snapshots, private fixtures, exported conversations, or local SQLite databases.
