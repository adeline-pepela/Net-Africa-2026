# Presentation Guide: Continuous Quality Monitoring for .NET AI Agents
### Amazon Bedrock AgentCore Evaluations · .NET Africa 2026

---

## The Big Idea (30 seconds)

You've built an AI agent. It answers questions, calls tools, sounds confident. How do you know it's actually correct?

**The problem:** An agent can regress silently. No exception. No error log. The answer just becomes wrong — and it still *reads well*. Traditional monitoring misses this entirely.

**The solution:** Treat agent quality like test coverage. Score every session with an LLM-as-a-judge, gate your PRs on it, and watch for drift in production.

This demo shows all three — in .NET, with ~30 lines of OpenTelemetry, no agent framework required.

---

## Architecture in One Diagram

```
         ┌──────────── Jozi Travel Agent (Bedrock Converse + 4 tools) ────────────┐
user ──► invoke_agent ──► chat ──► execute_tool get_weather_forecast ──► answer
         └──────── ActivitySource "opentelemetry.instrumentation.genai.dotnet" ───┘
                              │  session.id on every span
         ┌────────────────────┼──────────────────────┐
         ▼                    ▼                       ▼
  SessionSpanRecorder   SessionSpanRecorder    OTLP → CloudWatch
  (Demo 1: /eval)       (Demo 2: CI gate)      (Demo 3: online eval)
```

**Key insight:** AgentCore Evaluations only reads OpenTelemetry spans. It doesn't care whether you use Semantic Kernel, Microsoft.Extensions.AI, AutoGen, or a raw loop. If your spans follow the GenAI semantic conventions, it works.

---

## Project Structure

```
├── src/
│   ├── AgentCore.Evaluations.Otel/     ← The reusable library (copy into your own project)
│   ├── Jozi.TravelAgent/               ← The agent: Bedrock Converse loop + OTel instrumentation
│   ├── Jozi.TravelAgent.Chat/          ← Demo 1: interactive console (jozi-chat)
│   └── Jozi.TravelAgent.Evals/         ← Demo 2 & 3: CI gate + traffic + drift (jozi-evals)
├── config/
│   ├── agent.json                      ← Live config: prompt, model, tool descriptions
│   └── variants/
│       └── agent.regressed.json        ← The "bad PR" that fails the gate
├── evals/
│   ├── golden-dataset.json             ← 7 test cases with ground truth + thresholds
│   └── production-traffic.json         ← Simulated production conversations
├── .github/workflows/
│   └── agent-quality-gate.yml          ← Demo 2: PR check that posts a scorecard
└── infra/
    └── setup-online-eval.sh            ← Demo 3: CloudWatch alarm + online eval config
```

---

## The Agent: `src/Jozi.TravelAgent/`

### `TravelAgent.cs` — The Loop

This is a deliberately framework-free Bedrock Converse tool-use loop. The entire agent is one class with three methods:

- `AskAsync` — starts an `invoke_agent` span, drives the Converse loop, tags the final answer
- `ChatAsync` — starts a `chat` span per model call, tags input/output messages and token usage
- `ExecuteTool` — starts an `execute_tool` span per tool call, tags arguments and result

**The span tree for one user turn:**
```
invoke_agent JoziTravelAgent          ← one per user message
  ├── chat us.anthropic.claude-...    ← model decides to call a tool
  ├── execute_tool get_weather_forecast
  └── chat us.anthropic.claude-...    ← model writes the final answer
```

Every span gets `session.id` stamped on it via the `Tag()` helper — this is how AgentCore stitches individual traces into a conversation session.

### `TravelTools.cs` — Four Deterministic Tools

All data is hardcoded and fixed. This is intentional:

| Tool | Returns |
|------|---------|
| `get_weather_forecast` | 27°C / 15°C, thunderstorms, 60% rain (Johannesburg) |
| `search_flights` | HV245 at R1,450 CPT→JNB (cheapest) |
| `convert_currency` | Exactly 18.30 ZAR/USD |
| `get_visa_requirements` | Visa-free for Kenyan passport into South Africa |

Fixed data means the golden dataset can assert **exact answers**. Correctness scores only move when the agent's *behaviour* changes — not because an API returned a different rate today.

---

## The Instrumentation Library: `src/AgentCore.Evaluations.Otel/`

### `GenAi.cs` — The Most Important File

```csharp
public const string ScopeName = "opentelemetry.instrumentation.genai.dotnet";
public static readonly ActivitySource Source = new(ScopeName, "1.0.0");
```

**This one line is the gate.** AgentCore Evaluations routes spans by instrumentation scope name. The scope must start with `opentelemetry.instrumentation.` or `openinference.instrumentation.` — anything else is silently ignored, even if every attribute is perfect.

In .NET, the `ActivitySource` name *is* the OTel scope name. Change it to `"MyCompany.Agent"` and the Evaluate API returns:
```
ValidationException: Provided input has no spans with supported scope
```

The file also defines all the attribute names the evaluators read:
- `gen_ai.operation.name` — classifies the span as `invoke_agent`, `chat`, or `execute_tool`
- `session.id` — groups spans into a conversation
- `gen_ai.task.input` / `gen_ai.task.output` — user prompt and final answer on `invoke_agent`
- `gen_ai.input.messages` / `gen_ai.output.messages` — full message history on `chat`
- `gen_ai.tool.name`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` — on `execute_tool`

### `AgentTelemetry.cs` — Wiring OTel

Sets up the `TracerProvider` with three export modes:

| Mode | Used by | Where spans go |
|------|---------|----------------|
| `None` | Demo 1 & 2 | In-memory only (`SessionSpanRecorder`) |
| `CloudWatchOtlp` | Demo 3 | OTLP → CloudWatch Transaction Search via `SigV4Handler` |
| `Otlp` | Collector setup | Standard OTLP exporter |

Also sets the critical resource attribute:
```csharp
["aws.service.type"] = "gen_ai_agent"
```
Without this, online evaluation polls every 5 minutes, matches nothing, and never reports an error. ADOT (AWS's OTel distro for Python/JS) sets it automatically. Plain .NET OpenTelemetry does not.

### `AgentCoreEvaluator.cs` — Calling the Evaluate API

A thin wrapper over `bedrock-agentcore:Evaluate`. Takes a list of spans + optional ground truth, returns scores. The evaluators operate at three levels:

| Level | Evaluators | One score per |
|-------|-----------|---------------|
| SESSION | GoalSuccessRate, TrajectoryInOrderMatch | Whole conversation |
| TRACE | Correctness, Helpfulness | Each user turn |
| TOOL_CALL | ToolSelectionAccuracy, ToolParameterAccuracy | Each tool span |

---

## The Config Files

### `config/agent.json` — Baseline (v1.0.0)

```json
"systemPrompt": "You are Jozi... Always use the provided tools for weather forecasts, flights,
currency conversion and visa requirements. Never answer those from memory..."

"convert_currency": "Convert an amount between currencies using today's exchange rate."
```

The prompt explicitly instructs the agent to use tools. The tool description is clear and accurate.

### `config/variants/agent.regressed.json` — The Bad PR (v1.1.0)

```json
"systemPrompt": "...Tool calls add latency and cost, so answer directly from your own knowledge
whenever you reasonably can, and only call a tool as a last resort. Be confident and concise."

"convert_currency": "Legacy FX lookup. Slow and rarely needed; prefer well-known approximate rates."
```

Two small edits. Both look like reasonable cost/latency optimisations. The agent still runs. No errors. But currency answers become guesses (~R27,750 instead of R27,450 for 1500 USD).

**This is the entire point of the demo.**

---

## The Golden Dataset: `evals/golden-dataset.json`

7 test cases, each with:
- `turns` — the conversation (one or more user messages)
- `expectedResponse` — exact expected answer for the last turn (used by Correctness)
- `assertions` — behaviours the session should show (used by GoalSuccessRate)
- `expectedTools` — expected tool call sequence (used by TrajectoryInOrderMatch)

**Example case:**
```json
{
  "id": "usd-to-zar",
  "turns": ["My company gave me a 1500 US dollar travel budget. How much is that in rand?"],
  "expectedResponse": "1500 USD = 27,450.00 ZAR, using the conversion tool's rate of exactly 18.30 ZAR per USD.",
  "assertions": ["The agent used the currency conversion tool", "The converted amount is given in ZAR"],
  "expectedTools": ["convert_currency"]
}
```

**Thresholds:**
| Evaluator | Threshold |
|-----------|-----------|
| Correctness | 0.80 |
| GoalSuccessRate | 0.80 |
| TrajectoryInOrderMatch | 0.90 |
| ToolSelectionAccuracy | 0.90 |
| Helpfulness | 0.70 |

---

## Demo 1: Chat + Score a Session

```powershell
dotnet run --project src/Jozi.TravelAgent.Chat
```

**What to show:**
1. Ask: `What's the weather in Johannesburg on 2026-11-20? Do I need an umbrella?`
   → Agent calls `get_weather_forecast`, answers 27°C / thunderstorms / bring an umbrella
2. Ask: `And how much is 1500 dollars in rand?`
   → Agent calls `convert_currency`, answers R27,450
3. Type `/spans` → shows the span tree, writes `artifacts/<session>.spans.json`
4. Type `/eval` → scores the session live, prints score + label + judge's explanation

**Then show the regression:**
```powershell
dotnet run --project src/Jozi.TravelAgent.Chat -- --config config/variants/agent.regressed.json
```
Ask the same currency question. The agent answers "≈R27,750" without calling a tool. `/eval` shows no tool spans. The answer reads fine. **Nothing crashed. Nothing was logged as an error.**

---

## Demo 2: Fail the Pull Request

```powershell
# Baseline — should PASS
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/agent.json --dataset evals/golden-dataset.json

# Regressed — should FAIL
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/variants/agent.regressed.json --dataset evals/golden-dataset.json
```

**Expected results:**

| Evaluator | Threshold | Baseline | Regressed |
|-----------|-----------|----------|-----------|
| Correctness | 0.80 | 1.00 ✅ | 0.71 ❌ |
| GoalSuccessRate | 0.80 | 1.00 ✅ | 0.71 ❌ |
| TrajectoryInOrderMatch | 0.90 | 1.00 ✅ | 0.71 ❌ |
| ToolSelectionAccuracy | 0.90 | 1.00 ✅ | 1.00 ✅ |
| Helpfulness | 0.70 | 0.81 ✅ | 0.85 ✅ |

**Key talking point:** Helpfulness doesn't move. The guessed answer still reads well. Only ground-truth evaluators (Correctness, Trajectory) catch the regression. "Chat with it a few times and ship" would have missed this.

**The GitHub Actions workflow** (`.github/workflows/agent-quality-gate.yml`):
- Triggers on PRs that touch `config/**`, `src/**`, or `evals/**`
- Uses GitHub OIDC → no AWS keys stored in GitHub
- Runs the gate, writes `eval-report.md`, posts it as a sticky PR comment
- Exits with code 1 on failure → marks the check red → blocks merge if required

---

## Demo 3: Catch Drift in Production

```powershell
# Send baseline traffic (good config)
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --sessions 20

# Simulate "v1.1 release" (bad config, skipped the gate)
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --config config/variants/agent.regressed.json --sessions 15

# Check for drift
dotnet run --project src/Jozi.TravelAgent.Evals -- drift --results-log-group <log-group> --recent-minutes 30
```

**What drifts:** GoalSuccessRate drops from 1.00 → 0.70. Correctness stays at 1.00 (no ground truth in production). The judge's explanation: *"a convert_currency tool was available… but the assistant did not call it"*.

**The alarm** (`jozi-agent-goal-success-drop`) watches `Builtin.GoalSuccessRate` in CloudWatch Metrics → `Bedrock-AgentCore/Evaluations`. Wire `--alarm-actions` to SNS to get paged.

**Timing:** Online scores land ~6–7 minutes after a session ends (1-min session timeout + 5-min discovery poll + scoring). Run the v1.1 traffic at least 10 minutes before Demo 3.

---

## The Core Lesson

| What you check | What it catches |
|----------------|-----------------|
| Helpfulness alone | Nothing — guessed answers read well |
| Correctness (with ground truth) | Wrong answers, but only in CI |
| GoalSuccessRate (with assertions) | Skipped tools, wrong approach |
| TrajectoryInOrderMatch | Tool call order regressions |
| Online GoalSuccessRate | Production drift without ground truth |

**Gate on means over a dataset, not on single scores.** LLM judges are noisy. One score can vary ±0.1 between runs. A mean over 7 cases is stable enough to block a merge.

**Deterministic tools make ground truth cheap.** Because `convert_currency` always returns exactly 18.30 ZAR/USD, the expected answer is a constant. You write it once and it never needs updating.

---

## IAM Permissions Required

```json
{
  "Version": "2012-10-17",
  "Statement": [
    { "Effect": "Allow", "Action": ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream", "bedrock-agentcore:Evaluate"], "Resource": "*" },
    { "Effect": "Allow", "Action": ["logs:CreateLogGroup", "logs:PutLogEvents", "logs:StartQuery", "logs:GetQueryResults"], "Resource": "*" },
    { "Effect": "Allow", "Action": ["xray:PutTraceSegments", "xray:GetTraceSegmentDestination", "xray:UpdateTraceSegmentDestination"], "Resource": "*" },
    { "Effect": "Allow", "Action": ["aws-marketplace:ViewSubscriptions", "aws-marketplace:Subscribe"], "Resource": "*" }
  ]
}
```

---

## Quick Reference: All Commands

```powershell
# Build
dotnet build

# Demo 1: interactive chat
dotnet run --project src/Jozi.TravelAgent.Chat
dotnet run --project src/Jozi.TravelAgent.Chat -- --config config/variants/agent.regressed.json

# Demo 2: CI gate (local)
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/agent.json --dataset evals/golden-dataset.json
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/variants/agent.regressed.json --dataset evals/golden-dataset.json

# Demo 2: create the bad PR
git checkout -b perf/reduce-tool-calls
copy config\variants\agent.regressed.json config\agent.json
git commit -am "Reduce latency: prefer model knowledge over tool calls"
git push -u origin perf/reduce-tool-calls
gh pr create --fill

# Demo 3: production traffic
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --sessions 20
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --config config/variants/agent.regressed.json --sessions 15
dotnet run --project src/Jozi.TravelAgent.Evals -- drift --results-log-group <log-group> --recent-minutes 30
```

---

## Gotchas to Mention on Stage

1. **Scope name** — must start with `opentelemetry.instrumentation.` or evaluations silently ignore your spans
2. **`aws.service.type = "gen_ai_agent"`** — required for online evaluation to discover sessions; ADOT sets it automatically, plain .NET does not
3. **CloudWatch Transaction Search** — must be enabled (`aws xray get-trace-segment-destination` → `ACTIVE`); online evaluation reads from there
4. **`bedrock-agentcore:Evaluate`** — the exact IAM action name; `StartEvaluation` / `GetEvaluation` do not exist
5. **Timing** — online scores land ~6–7 minutes after session end; run production traffic before the demo, not during
