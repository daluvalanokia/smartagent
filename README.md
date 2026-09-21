# SmartAgent — scoped prompt generator (.NET 8)

SmartAgent inspects an application's source and returns **one consolidated,
precise prompt per run, limited to a prioritized scope** — tuned either toward
**improved functionality** or toward **fixing erratic behavior**.

## How a run works

```
source (a/b/c) → heuristic analysis → reasoning-site enrichment →
CONTINUITY MATCH (cross-run fingerprints, escalation, regressions) →
priority scoring → top-N scope → chained consolidated prompt
(AI provider, or built-in deterministic template when AI is offline)
→ feedback loop (CI reports fixed / wont_fix) → next run verifies
```

## Parallel prompt evaluator (scope/depth/width algorithm)

Paste ANY prompt (SmartAgent's own generated prompts or external ones) and the
evaluator automatically:

1. **Segments** it into independently evaluable work units — numbered items,
   finding references `[F001]`/fingerprints, bullets, markdown sections, or
   sentence groups for free prose (so nothing is unevaluable)
2. **Estimates depth** per unit (1–5): length, file spread and hard-domain
   signals (security, migration, concurrency, audit …) → per-unit cost
3. **Derives the width**: threads actually spawned = min(units, MaxThreads, CPU)
4. **Splits the work**: LPT (longest-processing-time-first) bin packing balances
   units into parallel lanes so no thread idles behind a heavy unit
5. **Runs the lanes concurrently** — one worker per lane, units within a lane
   sequential — with per-unit timeout, bounded retries and failure tolerance
   (a failed unit is recorded, never kills the run)
6. **Consolidates** all unit results into one report with a scope-satisfaction
   check: every planned unit must have a result (failures are accounted, gaps
   are not → `scopeSatisfied: false`)

- UI: `/Home/Evaluate`
- API: `POST /api/evaluate-prompt` `{ prompt, maxThreads?, retriesPerUnit? }`
  → plan (units, depth, lanes) + per-unit results + consolidated report

Deterministic by default (heuristic unit evaluator); swap `IWorkUnitEvaluator`
for an AI-backed implementation without touching the executor.

## CI/CD prompt pipeline (continuity)

Every run is part of a continuous loop per target:

- findings get stable **fingerprints** (target + file + rule + title hash),
  remembered across runs in a persisted registry (`data/continuity.json`)
- **new** findings appear once; **persisting** ones escalate (+15%/run, cap +45%)
- findings marked **fixed** via feedback are verified by the next run; if they
  reappear they become **REGRESSIONS** (1.5× boost, forced to the top of scope)
- findings marked **wont_fix** are suppressed from future prompts
- each generated prompt chains with the previous run: continuity summary,
  verification requirements, and next steps with the feedback endpoint
- a CI pipeline gates a build on `continuity.regressions == 0`

### HTTP API (for CI pipelines)

| Endpoint | Use |
|----------|-----|
| `POST /api/validate` | run a validation; body `{ sourceType: website\|github\|zipArchive, sourceRef, scopeLimit, focus, notes }` → runId, continuity diff, chained prompt, findings with fingerprints (zip source: sourceRef = base64 ZIP) |
| `GET /api/runs/{runId}` | stored run (prompt + findings + continuity) |
| `POST /api/runs/{runId}/feedback` | `{ feedback: [{ fingerprint, status: fixed\|wont_fix\|failed_verification, note }] }` — reports the outcome of applying the prompt |
| `GET /api/targets` | all targets with run counts and pending fix verifications |
| `GET /api/targets/{targetKey}/history` | full cross-run finding timeline for a target |
| `GET /api/health` | pipeline health |

Optional shared-secret guard: set `SmartAgent:ApiKey` in configuration and send
it as `X-Api-Key` on every call.

### Example: GitHub Actions step

```yaml
- name: SmartAgent validation
  run: |
    RESULT=$(curl -s -X POST $SMARTAGENT_URL/api/validate       -H "X-Api-Key: $SMARTAGENT_KEY" -H "Content-Type: application/json"       -d '{"sourceType":"github","sourceRef":"https://github.com/org/repo","scopeLimit":3,"focus":"stability"}')
    REGRESSIONS=$(echo "$RESULT" | jq '.continuity.regressions')
    echo "$RESULT" | jq -r '.prompt'
    if [ "$REGRESSIONS" != "0" ]; then echo "::error::regressions detected"; exit 1; fi
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
