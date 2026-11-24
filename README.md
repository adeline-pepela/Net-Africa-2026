# Continuous Quality Monitoring for .NET AI Agents with Amazon Bedrock AgentCore Evaluations

Demo code for the talk. A .NET 10 travel agent ("Jozi") for people attending a .NET conference in Johannesburg,
instrumented with OpenTelemetry and scored by **Amazon Bedrock AgentCore Evaluations** in three places:

| # | Where | What you show | Command |
|---|-------|---------------|---------|
| 1 | Laptop | Chat with the agent, then score **that session** with built-in evaluators | `jozi-chat` → `/eval` |
| 2 | Pull request | A harmless-looking prompt edit **fails the PR** in GitHub Actions | `jozi-evals gate` |
| 3 | Production | Online evaluation scores live traffic; a drift check catches the drop after release | `jozi-evals traffic` / `drift` |

No agent framework is used on purpose: it's a Bedrock Converse loop plus ~30 lines of OpenTelemetry.
AgentCore Evaluations only reads **spans**, so the same pattern works with Semantic Kernel, Microsoft.Extensions.AI, AutoGen or your own loop.

```
                 ┌──────────────── Jozi.TravelAgent (Bedrock Converse + 4 tools) ────────────────┐
 user ──► invoke_agent ──► chat ──► execute_tool get_weather_forecast ──► chat ──► answer
                 └───────── ActivitySource "opentelemetry.instrumentation.genai.dotnet" ─────────┘
                                     │  GenAI semantic conventions, session.id on every span
              ┌──────────────────────┼─────────────────────────────┐
              ▼                      ▼                             ▼
   SessionSpanRecorder      SessionSpanRecorder         OTLP exporter + SigV4Handler
   (in memory)              (in memory, CI)             (CloudWatch OTLP endpoint → Transaction Search)
              │                      │                             │
   Evaluate API             Evaluate API + ground truth   Online evaluation config (sampling)
   DEMO 1: /eval            DEMO 2: PR gate               DEMO 3: metrics, alarm, drift check
```

## Repo tour

| Path | What it is |
|------|------------|
| `src/AgentCore.Evaluations.Otel/` | **The reusable part.** Framework-agnostic: GenAI constants, span recorder, `SigV4Handler` (OTLP straight to CloudWatch, no collector), `AgentCoreEvaluator` (Evaluate API client). Copy this into your own project. |
| `src/AgentCore.Evaluations.Otel/GenAi.cs` | The scope-name rule: `opentelemetry.instrumentation.*` or evaluations ignore your spans. |
| `src/Jozi.TravelAgent/TravelAgent.cs` | The agent loop and its instrumentation (`invoke_agent` / `chat` / `execute_tool` spans). |
| `src/Jozi.TravelAgent/TravelTools.cs` | Four fake, deterministic tools, so expected answers are exact (27°C, R1,450, 18.30 ZAR/USD). |
| `src/Jozi.TravelAgent.Chat/` | Demo 1 console app (`jozi-chat`). |
| `src/Jozi.TravelAgent.Evals/` | Demo 2/3 CLI (`jozi-evals gate | traffic | drift`). |
| `config/agent.json` | Prompt, model and tool descriptions. The file a PR edits. |
| `config/variants/agent.regressed.json` | The "bad PR": a cost-saving prompt plus a tidied-up tool description. |
| `config/variants/agent.cheaper-model.json` | Optional "is it safe to swap to Haiku?" PR. |
| `evals/golden-dataset.json` | 7 cases with ground truth: expected answer, assertions, expected tool trajectory. Plus thresholds. |
| `evals/production-traffic.json` | Conversations for simulated production users. |
| `.github/workflows/agent-quality-gate.yml` | Demo 2 workflow: runs on PRs, posts a scorecard comment, fails below threshold. |
| `.github/workflows/agent-drift-check.yml` | Optional hourly drift report. |
| `infra/setup-online-eval.sh` | Demo 3 setup: log group, IAM role, online evaluation config, CloudWatch alarm. |
| `infra/setup-github-oidc.sh` | Demo 2 setup: GitHub OIDC role, so no AWS keys in GitHub. |
| `infra/otel-collector.yaml` | Alternative if you already run a collector: OTLP exporter → ADOT collector → CloudWatch. |

## Prerequisites

* .NET 10 SDK (`dotnet --list-sdks`; if you installed it with Microsoft's script, add `~/.dotnet` to `PATH`), AWS CLI v2, credentials for an account with Bedrock access in `us-east-1`
* Model access to `us.anthropic.claude-sonnet-4-6` (agent). Built-in evaluators need no extra model access.
* **CloudWatch Transaction Search enabled** (required by AgentCore Evaluations). Check: `aws xray get-trace-segment-destination` → `CloudWatchLogs / ACTIVE`.

```bash
dotnet build
```

---

## Demo 1: score one session (≈6 min)

```bash
dotnet run --project src/Jozi.TravelAgent.Chat
```



1. Ask: `What's the weather in Johannesburg on 2026-11-20? Do I need an umbrella?`
   You'll see `tools → get_weather_forecast` and an answer quoting 27°C / 15°C, thunderstorms.
2. Ask a follow-up: `And how much is 1500 dollars in rand?`
3. `/spans` prints the span tree and writes the exact JSON that will be sent to `artifacts/<session>.spans.json`.
   Point out: scope name, `gen_ai.operation.name`, `session.id` on every span, one `traceId` per turn.
4. `/eval` runs GoalSuccessRate (session), Correctness and Helpfulness (per turn), and ToolSelectionAccuracy and
   ToolParameterAccuracy (per tool call) in parallel, and prints the score, label and the judge's explanation for each.

**Contrast moment:** restart with the regressed config and ask the currency question again:

```bash
dotnet run --project src/Jozi.TravelAgent.Chat -- --config config/variants/agent.regressed.json
```

It confidently answers "≈R27,750" without calling a tool. `/eval` shows no tool spans, and the answer reads fine.
That's the point: **nothing crashed, nothing was logged as an error.**

**Rehearsal tip:** piped input works too, so you can replay a script:
`printf "What's the weather in Johannesburg on 2026-11-20?\n/eval\n/exit\n" | dotnet run --project src/Jozi.TravelAgent.Chat`

**Gotcha slide (optional, 30 s):** change `GenAi.ScopeName` to `"MyCompany.Agent"`, run, `/eval`, and you get:
`ValidationException: Provided input has no spans with supported scope`. The scope name is how instrumentation
opts in to a documented schema. In .NET, the `ActivitySource` name *is* the scope name.

## Demo 2: fail the pull request (≈8 min)

Local dry run (same command CI runs):

```bash
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/agent.json --dataset evals/golden-dataset.json
dotnet run --project src/Jozi.TravelAgent.Evals -- gate --config config/variants/agent.regressed.json --dataset evals/golden-dataset.json
```

Each run takes about 70–90 seconds. Results from rehearsal runs on this repo:

| Evaluator | Threshold | Baseline (3 runs) | Regressed (3 runs) |
|---|---:|---:|---:|
| Correctness | 0.80 | 1.00 | 0.75 ❌ |
| GoalSuccessRate | 0.80 | 1.00 | 0.71 ❌ |
| TrajectoryInOrderMatch | 0.90 | 1.00 | 0.71 ❌ |
| ToolSelectionAccuracy | 0.90 | 1.00 | 1.00 |
| Helpfulness | 0.70 | 0.81–0.86 | 0.81–0.85 |

With the regressed config, the agent stops calling `convert_currency` on two of the three currency questions and
guesses the rate instead ("≈R27,750"; the tool says R27,450). Ground truth catches it (trajectory, exact expected
amount, assertions). **Helpfulness doesn't move**: the guessed answer still reads well, which is exactly whyq

If Bedrock throttles, each case retries in a fresh session (up to 3 attempts). A case that still fails is reported as an
evaluation error and fails the gate, rather than crashing the build.

On stage with GitHub:

```bash
git checkout -b perf/reduce-tool-calls
cp config/variants/agent.regressed.json config/agent.json
git commit -am "Reduce latency: prefer model knowledge over tool calls"
git push -u origin perf/reduce-tool-calls
gh pr create --fill
git switch main
```

The **Agent quality gate** check runs (~3–4 min with restore and build) and goes red. A sticky comment shows the
scorecard, the per-case table and the judge's explanation for the lowest scores. With the check marked *required*, the PR can't merge.

One-time setup:

```bash
infra/setup-github-oidc.sh <owner>/<repo>
gh variable set AWS_EVAL_ROLE_ARN --body <role arn printed above> --repo <owner>/<repo>
```

Talking points: deterministic tools make ground truth cheap; thresholds are per evaluator; the gate costs about
35 judge calls per PR; and LLM judges are noisy, so gate on means over a dataset, not on single scores.

## Demo 3: catch drift in production (≈8 min)

One-time setup (creates a log group, an IAM role, an online evaluation config at 100% sampling with a 1-minute session timeout, and an alarm):

```bash
infra/setup-online-eval.sh
```

It prints the **results log group**.

> **Already set up in the demo account (us-east-1, created 2026-11-14):**
> online evaluation config `jozi_travel_agent_online-g6h6TqFIH0` (ACTIVE, 100% sampling, 1-minute timeout),
> IAM role `JoziOnlineEvaluationRole`, alarm `jozi-agent-goal-success-drop`,
> results log group `/aws/bedrock-agentcore/evaluations/results/jozi_travel_agent_online-g6h6TqFIH0`.

Then:

```bash
# Baseline traffic, ideally the day before or at least an hour before the talk
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --sessions 20

# "Release v1.1", which skipped the gate. Start this at the beginning of the talk so results are in by Demo 3.
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --config config/variants/agent.regressed.json --sessions 15

# Compare the last 30 minutes with the previous 24 hours
dotnet run --project src/Jozi.TravelAgent.Evals -- drift --results-log-group <results log group> --recent-minutes 30
```

Show:

* the drift table (baseline vs recent, delta, sessions) and the lowest-scoring recent sessions with the judge's explanation
* CloudWatch → Metrics → `Bedrock-AgentCore/Evaluations` → `Builtin.GoalSuccessRate` by `onlineEvaluationConfigId, service.name`
* the `jozi-agent-goal-success-drop` alarm (wire `--alarm-actions` to SNS to get paged)
* the AgentCore console → Evaluations → online configuration, with per-session results

What drifts (measured): with the bad v1.1 config, online **GoalSuccessRate fell from 1.00 to 0.70** (the judge: "a
convert_currency tool was available… but the assistant did not call it"), while **Correctness stayed at 1.00**. Without
an answer key, the judge accepts a plausible guessed rate. So the alarm watches GoalSuccessRate, and ground-truth
Correctness belongs in the CI gate.

Timing (measured): online scores land about **6–7 minutes** after a session ends (1-minute session timeout, plus a
5-minute discovery poll, plus scoring). So run the "v1.1" traffic at least 10 minutes before Demo 3, ideally at the
start of the talk. Rehearse on the venue Wi-Fi and keep a screenshot of a finished drift report as a fallback.

---

## How the spans are shaped (what AgentCore Evaluations needs)

Verified against the live Evaluate API while building this demo:

1. **Scope name** must start with `opentelemetry.instrumentation.` (OTel GenAI conventions) or `openinference.instrumentation.`.
2. **`gen_ai.operation.name`** on every span: `invoke_agent` (one per user turn), `chat` (model call), `execute_tool`.
3. **`session.id`** on every span. Spans group into traces (turns), and traces into a session.
4. **Content on the span** ("unified telemetry"): `gen_ai.task.input` / `gen_ai.task.output` on `invoke_agent`;
   `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`, `gen_ai.tool.definitions` on `chat`;
   `gen_ai.tool.name`, `gen_ai.tool.call.id`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result` on `execute_tool`.

5. **Resource attribute `aws.service.type = "gen_ai_agent"`** (plus `service.name`), required for online evaluation
   to discover sessions. ADOT adds it automatically; with plain .NET OpenTelemetry you must set it yourself.

Evaluator levels: **TRACE** (Correctness, Helpfulness, …: one score per turn), **TOOL_CALL** (ToolSelectionAccuracy,
ToolParameterAccuracy: one score per tool span), **SESSION** (GoalSuccessRate, Trajectory*: one score per conversation).

Getting spans to CloudWatch: `traffic` (and `jozi-chat --export cloudwatch`) use the standard OpenTelemetry OTLP exporter
pointed at the CloudWatch OTLP endpoint (`https://xray.<region>.amazonaws.com/v1/traces`), with a ~100-line
`SigV4Handler` doing the AWS request signing in-process. No collector or sidecar. Spans land in **Transaction Search**
(`aws/spans`), which is where **online evaluation discovers sessions** and which also feeds the CloudWatch GenAI
Observability dashboard.

> **Gotcha #3 for .NET (cost me an hour):** online evaluation discovers sessions with a CloudWatch Logs Insights query
> that filters on the resource attribute `aws.service.type = "gen_ai_agent"`. ADOT (AWS's OpenTelemetry distro, used by
> Python and JS agents) sets it for you; plain .NET OpenTelemetry doesn't. Without it the online config polls every
> 5 minutes, matches nothing, and reports no error. Batch evaluation doesn't apply that filter, which hides the problem.
> `AgentTelemetry` now sets it. I found the query in CloudTrail (`StartQuery` calls made by the evaluation role).
>
> The online-evaluation execution role also needs `bedrock-agentcore:StartBatchEvaluation`,
> `bedrock-agentcore:GetOnlineEvaluationConfig` and `logs:PutIndexPolicy`. `infra/online-eval-role-policy.json` has the full set.

## Cost and cleanup

* Each `Evaluate` call is one judge-model call (roughly 1–2K tokens). Demo 1 is about 8 calls; the gate about 35 per run.
* Online evaluation at 100% sampling scores every session with 4 evaluators. Drop to 5–10% outside the demo.

```bash
aws bedrock-agentcore-control delete-online-evaluation-config --online-evaluation-config-id jozi_travel_agent_online-g6h6TqFIH0
aws cloudwatch delete-alarms --alarm-names jozi-agent-goal-success-drop
aws iam delete-role-policy --role-name JoziOnlineEvaluationRole --policy-name online-evaluation
aws iam delete-role --role-name JoziOnlineEvaluationRole
aws logs delete-log-group --log-group-name /aws/bedrock-agentcore/evals-demo/dotnet-travel-agent
```

All airlines, flight numbers, fares, forecasts and visa notes in `TravelTools.cs` are made-up sample data.
