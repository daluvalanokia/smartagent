# SmartAgent — scoped prompt generator (.NET 8)

SmartAgent inspects an application's source and returns **one consolidated,
precise prompt per run, limited to a prioritized scope** — tuned either toward
**improved functionality** or toward **fixing erratic behavior**.

## How a run works

```
source (a/b/c) → heuristic analysis → reasoning-site enrichment →
priority scoring → top-N scope → consolidated prompt (AI provider, or
built-in deterministic template when AI is offline)
```

## Source inputs

| Mode | Input | What happens |
|------|-------|--------------|
| a. Source as ZIP | upload a `.zip` | Code/text files extracted (binaries, `node_modules`, `bin/obj` etc. skipped), capped at 400 files / 8 MB |
| b. Source from GitHub | repo URL (`https://github.com/owner/repo`, optional `/tree/branch`) | Default-branch tarball fetched and extracted |
| c. App from website URL | live URL | Same-site crawl (up to 12 pages), HTML captured for behavior analysis |

## Project layout

```
SmartAgent.sln
src/
  SmartAgent.Domain           models: SourceSnapshot, Finding, RunOptions, PromptRunResult
  SmartAgent.SourceProviders  ZIP / GitHub / Website providers (ISourceProvider)
  SmartAgent.Integrations     AI processing provider (OpenAI-compatible) + reasoning-site endpoints
  SmartAgent.Core             HeuristicAnalyzer, Prioritizer, PromptBuilder, SmartAgentEngine
  SmartAgent.Web              ASP.NET Core 8 MVC UI (upload form, result view, .txt download)
tests/
  SmartAgent.Tests            xUnit tests (prioritization, scoping, analyzer, prompt building, URL parsing)
```

## The scope contract

One run never tries to fix everything:

- `ScopeLimit` (1–8) — how many findings are consolidated into the run's prompt
- `Focus` — *Balanced*, *Functionality* (boost functionality improvements) or
  *Stability* (boost erratic-behavior findings)
- findings are de-duplicated, scored (`severity × category × focus boost ×
  reasoning nudge`), and only the top slice enters the prompt
- operator notes are carried through to the prompt

## Integrations

### AI processing provider (prompt consolidation)
OpenAI-compatible (`/chat/completions`) — works with OpenAI, Azure OpenAI,
OpenRouter, Ollama, etc. Configure in `appsettings.json`:

```json
"SmartAgent": {
  "Ai": {
    "BaseUrl": "https://api.openai.com/v1",
    "ApiKey": "<your key>",
    "Model": "gpt-4o-mini"
  }
}
```

If the provider is not configured or unreachable, SmartAgent falls back to a
deterministic template — a run never fails because AI is down.

### Smart logic / reasoning websites (enrichment)
Any number of endpoints, called in parallel with the current findings.
Response contract:

```json
{ "notes": [ { "findingId": "F001", "note": "known regression in 2.3", "severityAdjust": 1 } ] }
```

```json
"SmartAgent": {
  "ReasoningSites": [
    { "Name": "my-reasoning-service", "Endpoint": "https://logic.example.com/api/review", "ApiKey": "", "TimeoutSeconds": 10 }
  ]
}
```

Unreachable sites degrade to warnings — the run continues.

## Build & run

```bash
dotnet build SmartAgent.sln -c Release
dotnet test SmartAgent.sln -c Release          # 19 tests
dotnet run --project src/SmartAgent.Web          # http://localhost:5000
```

.NET 8 SDK required. No database needed — run results are held in a bounded
in-memory store (last 50 runs).
