# Your .NET AI Agent Lied to You — And You Had No Idea

> *It answered confidently. It sounded helpful. It was completely wrong. And nothing in your logs showed it.*

That's the problem this article solves.

We're going to build a .NET 10 travel agent, instrument it with OpenTelemetry in about 30 lines, and then wire it up to **Amazon Bedrock AgentCore Evaluations** — a service that uses an LLM as a judge to score your agent's answers, tool usage, and goal completion. We'll gate pull requests on those scores, and catch silent regressions in production before your users do.

By the end you'll have:
- A working AI agent (Bedrock Converse + 4 tools, no framework)
- OTel instrumentation that any evaluator can read
- A PR quality gate that fails when agent quality drops
- A production drift detector that catches regressions after deploy

No fluff. Let's build.

---

## The Problem Nobody Talks About

When a traditional API breaks, you know. You get a 500, an exception, a failed health check. Your on-call alert fires.

When an AI agent regresses, you get... nothing. The agent still runs. It still returns HTTP 200. It still sounds confident. It just starts answering from memory instead of calling your tools — and the answers are *almost* right.

Here's the exact scenario we'll reproduce:

**Before the "optimisation":**
> User: "How much is 1500 USD in rand?"
> Agent: *calls convert_currency tool* → "1500 USD = **R27,450** at today's rate of 18.30 ZAR/USD."

**After a well-meaning prompt edit:**
> User: "How much is 1500 USD in rand?"
> Agent: *no tool call* → "1500 USD is approximately **R27,750**."

The answer is wrong by R300. No exception. No error log. Helpfulness score: unchanged. The PR looked fine.

This is what we're building defences against.

---

## What We're Building

Meet **Jozi** — a travel assistant for .NET conference attendees flying into Johannesburg. She can check weather, search flights, convert currencies, and look up visa requirements.

```
┌─────────────────────────────────────────────────────────────────┐
│              Jozi Travel Agent                                  │
│         (Bedrock Converse + 4 tools)                            │
│                                                                 │
│  user ──► invoke_agent ──► chat ──► execute_tool ──► answer     │
│                                                                 │
│  ActivitySource: opentelemetry.instrumentation.genai.dotnet     │
└──────────────────────┬──────────────────────────────────────────┘
                       │  session.id on every span
          ┌────────────┼────────────────────┐
          ▼            ▼                    ▼
   In-memory      In-memory          OTLP → CloudWatch
   recorder       recorder           Transaction Search
          │            │                    │
   /eval command   CI gate             Online evaluation
   (Demo 1)        (Demo 2)            (Demo 3)
```

Three demos, one codebase:

| Demo | What it shows | Command |
|------|--------------|---------|
| 1 | Chat with the agent, score the session live | `dotnet run` → `/eval` |
| 2 | A bad prompt edit fails the PR in GitHub Actions | `jozi-evals gate` |
| 3 | Production drift caught after a silent regression ships | `jozi-evals drift` |

---

## Prerequisites

Before we start, make sure you have:

- **.NET 10 SDK** — `dotnet --list-sdks` should show `10.x.x`
- **AWS CLI v2** — `aws --version`
- **AWS credentials** configured for `us-east-1` with Bedrock access
- **Claude Sonnet 4** accessible in your account (invoke it once from the Bedrock playground to activate)

Check your setup:

```bash
dotnet --list-sdks        # needs 10.x.x
aws sts get-caller-identity   # needs to return your account ID
aws xray get-trace-segment-destination  # needs CloudWatchLogs / ACTIVE
```

If the X-Ray destination isn't active:
```bash
aws xray update-trace-segment-destination --destination CloudWatchLogs
```

**IAM policy** for your user/role (minimum required):

```json
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": [
        "bedrock:InvokeModel",
        "bedrock:InvokeModelWithResponseStream",
        "bedrock-agentcore:Evaluate"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "logs:CreateLogGroup", "logs:PutLogEvents",
        "logs:StartQuery", "logs:GetQueryResults"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": [
        "xray:PutTraceSegments",
        "xray:GetTraceSegmentDestination",
        "xray:UpdateTraceSegmentDestination"
      ],
      "Resource": "*"
    },
    {
      "Effect": "Allow",
      "Action": ["aws-marketplace:ViewSubscriptions", "aws-marketplace:Subscribe"],
      "Resource": "*"
    }
  ]
}
```

---

## Get the Code

```bash
git clone https://github.com/<your-repo>/jozi-travel-agent
cd jozi-travel-agent
dotnet build
```

The solution structure:

```
src/
├── AgentCore.Evaluations.Otel/     ← reusable OTel + Evaluate API library
│   ├── GenAi.cs                    ← THE most important file (scope name + attribute names)
│   ├── AgentTelemetry.cs           ← TracerProvider setup, export modes
│   ├── SessionSpanRecorder.cs      ← in-memory span store for on-demand eval
│   ├── AgentCoreEvaluator.cs       ← thin wrapper over bedrock-agentcore:Evaluate
│   └── SigV4Handler.cs             ← signs OTLP requests to CloudWatch (no collector needed)
├── Jozi.TravelAgent/
│   ├── TravelAgent.cs              ← the agent loop (~100 lines, no framework)
│   └── TravelTools.cs              ← 4 deterministic tools
├── Jozi.TravelAgent.Chat/          ← Demo 1 console app
└── Jozi.TravelAgent.Evals/         ← Demo 2 & 3 CLI
config/
├── agent.json                      ← live config: prompt, model, tool descriptions
└── variants/
    └── agent.regressed.json        ← the "bad PR"
evals/
├── golden-dataset.json             ← 7 test cases with ground truth
└── production-traffic.json         ← simulated production conversations
.github/workflows/
└── agent-quality-gate.yml          ← Demo 2: PR check
```

---

## Part 1: The Agent

### The Tools — Why Deterministic Data Matters

`TravelTools.cs` has four tools with completely hardcoded, fixed data:

```csharp
// Currency rates — always exactly these values
private static readonly Dictionary<string, decimal> PerUsd = new()
{
    ["USD"] = 1m,
    ["ZAR"] = 18.30m,   // always 18.30, never changes
    ["EUR"] = 0.92m,
    ["KES"] = 129.00m,
    ["NGN"] = 1550.00m,
};

// Flights — always the same options
[(\"Cape Town\", \"Johannesburg\")] = [
    new("HV245", "Highveld Air", "06:30", "08:35", 0, fare_zar: 1450),  // cheapest
    new("PA312", "Protea Airways", "09:00", "11:05", 0, fare_zar: 2180),
    new("BX822", "Baobab Express", "17:15", "19:20", 0, fare_zar: 1690),
],
```

**Why fake, fixed data?** Because it makes ground truth trivially cheap to write. The expected answer for "1500 USD in rand" is always `R27,450.00`. It never changes. You write it once in your golden dataset and it's valid forever.

If you used a real exchange rate API, your expected answer would be wrong tomorrow. You'd spend more time maintaining test data than writing tests.

### The Agent Loop — 30 Lines of OTel

`TravelAgent.cs` is a raw Bedrock Converse loop. No Semantic Kernel. No LangChain. Just the AWS SDK and OpenTelemetry `Activity` calls.

Here's the span tree it produces for one user turn:

```
invoke_agent JoziTravelAgent          ← one Activity per user message
  ├── chat us.anthropic.claude-...    ← model decides to call a tool
  ├── execute_tool get_weather_forecast
  └── chat us.anthropic.claude-...    ← model writes the final answer
```

The instrumentation for `AskAsync`:

```csharp
public async Task<AgentTurn> AskAsync(string userInput, CancellationToken ct = default)
{
    // Start the top-level span for this user turn
    using var turn = GenAi.Source.StartActivity($"invoke_agent {_config.Name}");
    Tag(turn, GenAi.OperationName, GenAi.OpInvokeAgent);  // "gen_ai.operation.name" = "invoke_agent"
    Tag(turn, GenAi.TaskInput, userInput);                 // "gen_ai.task.input" = the user's question

    // ... Converse loop runs here ...

    Tag(turn, GenAi.TaskOutput, answer);   // "gen_ai.task.output" = the final answer
    return new AgentTurn(answer, turn?.TraceId.ToHexString() ?? "", toolsCalled);
}
```

And the critical helper that stamps `session.id` on every span:

```csharp
private void Tag(Activity? activity, string key, object value)
{
    if (activity is null) return;
    // Every span gets session.id — this is how AgentCore stitches traces into a session
    if (activity.GetTagItem(GenAi.SessionId) is null)
        activity.SetTag(GenAi.SessionId, SessionId);
    activity.SetTag(key, value);
}
```

This is the entire secret. Every span in a conversation shares the same `session.id`. AgentCore groups spans by `session.id` into sessions, and sessions are what get evaluated.

---

## Part 2: The Instrumentation Library

### `GenAi.cs` — The Most Important File in the Repo

```csharp
public static class GenAi
{
    // THE most important line.
    // AgentCore Evaluations routes spans by instrumentation scope name.
    // Must start with "opentelemetry.instrumentation." or your spans are silently ignored.
    // In .NET, the ActivitySource name IS the OTel scope name.
    public const string ScopeName = "opentelemetry.instrumentation.genai.dotnet";

    public static readonly ActivitySource Source = new(ScopeName, "1.0.0");

    // Span types
    public const string OperationName = "gen_ai.operation.name";
    public const string OpInvokeAgent = "invoke_agent";
    public const string OpChat        = "chat";
    public const string OpExecuteTool = "execute_tool";

    // Session grouping — every span must carry this
    public const string SessionId = "session.id";

    // Content attributes — what the judge reads
    public const string TaskInput        = "gen_ai.task.input";
    public const string TaskOutput       = "gen_ai.task.output";
    public const string InputMessages    = "gen_ai.input.messages";
    public const string OutputMessages   = "gen_ai.output.messages";
    public const string SystemInstructions = "gen_ai.system_instructions";
    public const string ToolDefinitions  = "gen_ai.tool.definitions";
    public const string ToolName         = "gen_ai.tool.name";
    public const string ToolCallArguments = "gen_ai.tool.call.arguments";
    public const string ToolCallResult   = "gen_ai.tool.call.result";
}
```

> **Gotcha #1:** Change `ScopeName` to `"MyCompany.Agent"` and the Evaluate API returns:
> `ValidationException: Provided input has no spans with supported scope`
> The scope name is how your instrumentation opts into the GenAI semantic conventions schema.

### `AgentTelemetry.cs` — Wiring It All Together

```csharp
public static AgentTelemetry Start(AgentTelemetryOptions options)
{
    var builder = Sdk.CreateTracerProviderBuilder()
        .SetResourceBuilder(ResourceBuilder.CreateDefault()
            .AddService(options.ServiceName)
            .AddAttributes(new Dictionary<string, object>
            {
                ["deployment.environment.name"] = options.Environment,
                // CRITICAL for online evaluation — ADOT sets this automatically,
                // plain .NET OpenTelemetry does NOT. Without it, online eval
                // polls every 5 minutes, matches nothing, and never reports an error.
                ["aws.service.type"] = "gen_ai_agent",
            }))
        .AddSource(GenAi.ScopeName);

    // In-memory recorder for on-demand evaluation (Demo 1 & 2)
    if (options.RecordInMemory)
    {
        recorder = new SessionSpanRecorder();
        builder.AddProcessor(recorder);
    }

    // For Demo 3: OTLP straight to CloudWatch, SigV4-signed in-process
    if (options.Export == SpanExport.CloudWatchOtlp)
    {
        builder.AddOtlpExporter(otlp =>
        {
            otlp.Endpoint = new Uri($"https://xray.{options.Region}.amazonaws.com/v1/traces");
            otlp.Protocol = OtlpExportProtocol.HttpProtobuf;
            otlp.HttpClientFactory = () => new HttpClient(new SigV4Handler(options.Region, "xray"));
        });
    }
}
```

> **Gotcha #2:** `aws.service.type = "gen_ai_agent"` is required for online evaluation to discover sessions. Online evaluation uses a CloudWatch Logs Insights query that filters on this attribute. Without it, the online config silently matches nothing.

### `SessionSpanRecorder.cs` — In-Memory Span Store

```csharp
public sealed class SessionSpanRecorder : BaseProcessor<Activity>
{
    private readonly ConcurrentDictionary<string, ConcurrentQueue<JsonObject>> _spansBySession = new();

    public override void OnEnd(Activity activity)
    {
        // Only record spans that carry a session.id
        if (activity.GetTagItem(GenAi.SessionId) is not string sessionId) return;

        var span = CloudWatchSpanFormatter.Format(activity, resource.Attributes);
        _spansBySession.GetOrAdd(sessionId, _ => new ConcurrentQueue<JsonObject>()).Enqueue(span);
    }

    public IReadOnlyList<JsonObject> GetSessionSpans(string sessionId) =>
        _spansBySession.TryGetValue(sessionId, out var queue)
            ? queue.OrderBy(s => s["startTimeUnixNano"]!.GetValue<long>()).ToList()
            : [];
}
```

This is what makes Demo 1 and 2 instant. Instead of waiting 5–7 minutes for spans to land in CloudWatch and be discovered by the online evaluator, we keep them in memory and call the Evaluate API directly with the raw span JSON.

---

## Part 3: The Config Files — Where the Regression Lives

### `config/agent.json` — The Good Config (v1.0.0)

```json
{
  "name": "JoziTravelAgent",
  "version": "1.0.0",
  "modelId": "us.anthropic.claude-sonnet-4-6",
  "systemPrompt": "You are Jozi, a travel assistant... Always use the provided tools for weather forecasts, flights, currency conversion and visa requirements. Never answer those from memory, because your knowledge may be out of date.",
  "toolDescriptions": {
    "convert_currency": "Convert an amount between currencies using today's exchange rate. Supports ZAR, USD, EUR, GBP, KES and NGN."
  }
}
```

Key phrase: **"Always use the provided tools... Never answer those from memory."**

### `config/variants/agent.regressed.json` — The Bad PR (v1.1.0)

```json
{
  "version": "1.1.0",
  "systemPrompt": "You are Jozi... Tool calls add latency and cost, so answer directly from your own knowledge whenever you reasonably can, and only call a tool as a last resort. Be confident and concise.",
  "toolDescriptions": {
    "convert_currency": "Legacy FX lookup. Slow and rarely needed; prefer well-known approximate rates."
  }
}
```

Two edits. Both look reasonable. The PR description says "Reduce latency: prefer model knowledge over tool calls."

- The system prompt now discourages tool calls
- The `convert_currency` description is rewritten to make the tool sound unreliable

The model reads both. It stops calling `convert_currency` for currency questions and guesses instead. The guesses are close but wrong. **Nothing crashes.**

---

## Part 4: The Golden Dataset

`evals/golden-dataset.json` has 7 test cases. Each one has:

```json
{
  "id": "usd-to-zar",
  "turns": ["My company gave me a 1500 US dollar travel budget. How much is that in rand?"],
  "expectedResponse": "1500 USD = 27,450.00 ZAR, using the conversion tool's rate of exactly 18.30 ZAR per USD. A different amount or rate is incorrect.",
  "assertions": [
    "The agent used the currency conversion tool",
    "The converted amount is given in ZAR"
  ],
  "expectedTools": ["convert_currency"]
}
```

- `expectedResponse` → fed to **Correctness** evaluator (did the agent get the right answer?)
- `assertions` → fed to **GoalSuccessRate** (did the agent behave correctly?)
- `expectedTools` → fed to **TrajectoryInOrderMatch** (did the agent call the right tools?)

**Thresholds** (also in the dataset):

```json
"thresholds": {
  "Builtin.Correctness":            0.80,
  "Builtin.GoalSuccessRate":        0.80,
  "Builtin.TrajectoryInOrderMatch": 0.90,
  "Builtin.ToolSelectionAccuracy":  0.90,
  "Builtin.Helpfulness":            0.70
}
```

The gate fails if any evaluator's **mean score across all cases** drops below its threshold. You're gating on means, not individual scores — LLM judges are noisy, a single score can vary ±0.1 between runs.

---

## Demo 1: Chat With the Agent and Score It Live

```bash
dotnet run --project src/Jozi.TravelAgent.Chat
```

You'll see the Jozi ASCII banner and a prompt. Try these questions in order:

**Step 1:** Ask about the weather
```
you> What's the weather in Johannesburg on 2026-11-20? Do I need an umbrella?
```

The agent calls `get_weather_forecast` and answers: 27°C high, 15°C low, afternoon thunderstorms, 60% chance of rain. Yes, bring an umbrella.

**Step 2:** Ask about currency
```
you> And how much is 1500 dollars in rand?
```

The agent calls `convert_currency` and answers: R27,450.00 at 18.30 ZAR/USD.

**Step 3:** Inspect the spans
```
you> /spans
```

This prints the span tree and writes `artifacts/<session-id>.spans.json`. You'll see:

```
┌─────────────────────────────────────────────────────────────────┐
│ Span                          │ gen_ai.operation.name │ traceId │
├───────────────────────────────┼───────────────────────┼─────────┤
│ invoke_agent JoziTravelAgent  │ invoke_agent           │ a1b2c3  │
│ chat us.anthropic.claude-...  │ chat                   │ a1b2c3  │
│ execute_tool get_weather_...  │ execute_tool           │ a1b2c3  │
│ chat us.anthropic.claude-...  │ chat                   │ a1b2c3  │
│ invoke_agent JoziTravelAgent  │ invoke_agent           │ d4e5f6  │
│ chat us.anthropic.claude-...  │ chat                   │ d4e5f6  │
│ execute_tool convert_currency │ execute_tool           │ d4e5f6  │
│ chat us.anthropic.claude-...  │ chat                   │ d4e5f6  │
└─────────────────────────────────────────────────────────────────┘
```

Two traces (one per user turn), all sharing the same `session.id`. This is exactly what gets sent to the Evaluate API.

**Step 4:** Score the session
```
you> /eval
```

AgentCore runs 5 evaluators in parallel and prints results like:

```
┌──────────────────────────┬───────┬───────────┬──────────────────────────────────────────┐
│ Evaluator                │ Score │ Label     │ Explanation                              │
├──────────────────────────┼───────┼───────────┼──────────────────────────────────────────┤
│ GoalSuccessRate          │ 1.00  │ Excellent │ Agent used tools as instructed...        │
│ Correctness (turn 1)     │ 1.00  │ Correct   │ Temperature and conditions match...      │
│ Correctness (turn 2)     │ 1.00  │ Correct   │ Amount matches expected R27,450...       │
│ ToolSelectionAccuracy    │ 1.00  │ Excellent │ Correct tools selected for both queries  │
│ Helpfulness (turn 1)     │ 0.83  │ Good      │ Clear and actionable answer...           │
└──────────────────────────┴───────┴───────────┴──────────────────────────────────────────┘
```

### Now Show the Regression

Restart with the bad config:

```bash
dotnet run --project src/Jozi.TravelAgent.Chat -- --config config/variants/agent.regressed.json
```

Ask the same currency question:
```
you> How much is 1500 dollars in rand?
```

The agent answers: *"1500 USD is approximately R27,750."* — no tool call, wrong answer.

Type `/eval`. The scores tell the story:

```
┌──────────────────────────┬───────┬───────────┬──────────────────────────────────────────┐
│ Evaluator                │ Score │ Label     │ Explanation                              │
├──────────────────────────┼───────┼───────────┼──────────────────────────────────────────┤
│ GoalSuccessRate          │ 0.00  │ Failed    │ convert_currency tool was available but  │
│                          │       │           │ the assistant did not call it             │
│ Correctness              │ 0.00  │ Incorrect │ Expected R27,450, got ~R27,750           │
│ Helpfulness              │ 0.83  │ Good      │ Answer is clear and well-formatted       │
└──────────────────────────┴───────┴───────────┴──────────────────────────────────────────┘
```

**Helpfulness: 0.83. Still "Good."** The answer reads well. The judge can't tell it's wrong without the expected answer. This is exactly why you need ground-truth evaluators in your CI gate.

---

## Demo 2: Fail the Pull Request

### The Gate Command

```bash
# Baseline — should PASS
dotnet run --project src/Jozi.TravelAgent.Evals -- gate \
  --config config/agent.json \
  --dataset evals/golden-dataset.json

# Regressed — should FAIL
dotnet run --project src/Jozi.TravelAgent.Evals -- gate \
  --config config/variants/agent.regressed.json \
  --dataset evals/golden-dataset.json
```

Each run takes about 70–90 seconds. It runs all 7 cases in parallel (3 at a time), captures spans in memory, and calls the Evaluate API for each case.

**Baseline result:**

```
╭────────────────────────┬──────┬───────────┬────────┬────────╮
│ Evaluator              │ Mean │ Threshold │ Scored │ Result │
├────────────────────────┼──────┼───────────┼────────┼────────┤
│ Correctness            │ 1.00 │   0.80    │   7    │ pass ✅ │
│ GoalSuccessRate        │ 1.00 │   0.80    │   7    │ pass ✅ │
│ TrajectoryInOrderMatch │ 1.00 │   0.90    │   7    │ pass ✅ │
│ ToolSelectionAccuracy  │ 1.00 │   0.90    │   7    │ pass ✅ │
│ Helpfulness            │ 0.83 │   0.70    │   7    │ pass ✅ │
╰────────────────────────┴──────┴───────────┴────────┴────────╯
PASS agent quality is within thresholds.
```

**Regressed result:**

```
╭────────────────────────┬──────┬───────────┬────────┬────────╮
│ Evaluator              │ Mean │ Threshold │ Scored │ Result │
├────────────────────────┼──────┼───────────┼────────┼────────┤
│ Correctness            │ 0.71 │   0.80    │   7    │ FAIL ❌ │
│ GoalSuccessRate        │ 0.83 │   0.80    │   6    │ pass ✅ │
│ TrajectoryInOrderMatch │ 0.60 │   0.90    │   5    │ FAIL ❌ │
│ ToolSelectionAccuracy  │ 1.00 │   0.90    │   8    │ pass ✅ │
│ Helpfulness            │ 0.88 │   0.70    │   7    │ pass ✅ │
╰────────────────────────┴──────┴───────────┴────────┴────────╯
FAIL agent quality dropped below threshold. Blocking merge.
```

The `usd-to-zar` and `eur-to-zar` cases both show `no tools` — the agent skipped `convert_currency` and guessed. Correctness and Trajectory catch it. Helpfulness doesn't move.

### The GitHub Actions Workflow

`.github/workflows/agent-quality-gate.yml`:

```yaml
name: Agent quality gate

on:
  pull_request:
    paths: ["config/**", "src/**", "evals/**"]

permissions:
  id-token: write       # OIDC to AWS — no long-lived keys in GitHub
  contents: read
  pull-requests: write  # post the scorecard comment

jobs:
  evaluate:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: "10.0.x"

      - uses: aws-actions/configure-aws-credentials@v4
        with:
          role-to-assume: ${{ vars.AWS_EVAL_ROLE_ARN }}
          aws-region: us-east-1

      - name: Score the agent with AgentCore Evaluations
        id: gate
        run: >
          dotnet run --project src/Jozi.TravelAgent.Evals -c Release --
          gate
          --config config/agent.json
          --dataset evals/golden-dataset.json
          --report eval-report.md

      - name: Post scorecard on the PR
        if: always() && github.event_name == 'pull_request'
        uses: marocchino/sticky-pull-request-comment@v2
        with:
          header: agent-quality-gate
          path: eval-report.md
```

Key design decisions:
- **OIDC authentication** — no AWS access keys stored in GitHub secrets
- **Sticky PR comment** — the scorecard updates in place on every push, doesn't spam the thread
- **`if: always()`** — the comment posts even when the gate fails
- **Required check** — mark this check as required in branch protection and the bad PR literally cannot merge

### Setting Up the GitHub OIDC Role

```bash
bash infra/setup-github-oidc.sh <owner>/<repo>
# Prints the role ARN

gh variable set AWS_EVAL_ROLE_ARN --body <role-arn> --repo <owner>/<repo>
```

### Triggering the Bad PR

```bash
git checkout -b perf/reduce-tool-calls
cp config/variants/agent.regressed.json config/agent.json
git commit -am "Reduce latency: prefer model knowledge over tool calls"
git push -u origin perf/reduce-tool-calls
gh pr create --fill
git switch main
```

The **Agent quality gate** check runs in ~3–4 minutes. It goes red. The sticky comment shows the full scorecard, the per-case table, and the judge's explanation for the lowest scores. With the check marked required, the PR is blocked.

---

## Demo 3: Catch Drift in Production

The CI gate catches regressions before they ship. But what about the regression that *already shipped* — the one that slipped through because someone merged directly to main, or the gate wasn't required yet, or the regression only shows up under real user traffic patterns?

That's what online evaluation is for.

### How Online Evaluation Works

```
User sessions
     │
     ▼
Jozi agent (with CloudWatchOtlp export)
     │
     ▼  OTLP + SigV4
CloudWatch Transaction Search (aws/spans)
     │
     ▼  polls every 5 minutes
Online evaluation config
     │  (100% sampling, 1-min session timeout)
     ▼
Scores written to results log group
     │
     ├──► CloudWatch Metrics (Bedrock-AgentCore/Evaluations)
     │         │
     │         ▼
     │    CloudWatch Alarm (jozi-agent-goal-success-drop)
     │         │
     │         ▼
     │       SNS → PagerDuty / Slack / email
     │
     └──► Drift check (jozi-evals drift)
```

Spans flow from the agent to CloudWatch Transaction Search via OTLP. The online evaluation config polls for completed sessions (idle for more than the session timeout), scores them with the configured evaluators, and writes results to a log group. CloudWatch Metrics picks up the scores automatically.

### One-Time Setup

```bash
bash infra/setup-online-eval.sh
```

This creates:
- A CloudWatch log group for spans
- An IAM role for the online evaluation config
- An online evaluation config at 100% sampling with a 1-minute session timeout
- A CloudWatch alarm on `Builtin.GoalSuccessRate`

It prints the results log group name — save it, you'll need it for the drift command.

### Send Baseline Traffic

```bash
# Good config — 20 sessions of realistic production conversations
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --sessions 20
```

This runs conversations from `evals/production-traffic.json` in parallel, exporting spans to CloudWatch via OTLP. Each session is a realistic multi-turn conversation: "I'm flying from Lagos to Johannesburg, find me a flight, convert the fare to naira, and check if I need a visa."

Wait ~7 minutes for the online evaluator to score the sessions (1-min session timeout + 5-min discovery poll + scoring time).

### "Release v1.1" — The Bad Config Ships

```bash
# Simulate a release that skipped the gate
dotnet run --project src/Jozi.TravelAgent.Evals -- traffic \
  --config config/variants/agent.regressed.json \
  --sessions 15
```

Same conversations, bad config. The agent stops calling `convert_currency` and guesses exchange rates.

### Check for Drift

```bash
dotnet run --project src/Jozi.TravelAgent.Evals -- drift \
  --results-log-group /aws/bedrock-agentcore/evaluations/results/<your-config-id> \
  --recent-minutes 30
```

Output:

```
Production drift check · online evaluation results
log group /aws/bedrock-agentcore/evaluations/results/jozi_travel_agent_online-...
baseline  08:00 → 09:30 UTC
recent    09:30 → 10:00 UTC

╭──────────────────────┬──────────┬────────┬───────┬──────────────────────╮
│ Evaluator            │ Baseline │ Recent │   Δ   │ Status               │
├──────────────────────┼──────────┼────────┼───────┼──────────────────────┤
│ GoalSuccessRate      │   1.00   │  0.70  │ -0.30 │ DRIFT ❌             │
│ Helpfulness          │   0.84   │  0.85  │ +0.01 │ ok ✅                │
╰──────────────────────┴──────────┴────────┴───────┴──────────────────────╯

Lowest recent scores:
╭──────────────────┬───────┬──────────────┬──────────────────────────────────────────────╮
│ Evaluator        │ Score │ Session      │ Why                                          │
├──────────────────┼───────┼──────────────┼──────────────────────────────────────────────┤
│ GoalSuccessRate  │ 0.00  │ jozi-a1b2... │ A convert_currency tool was available but    │
│                  │       │              │ the assistant did not call it. The rate used  │
│                  │       │              │ appears to be from model memory.             │
╰──────────────────┴───────┴──────────────┴──────────────────────────────────────────────╯

Quality drift detected (an evaluator dropped by more than 0.15).
```

**GoalSuccessRate: 1.00 → 0.70. Helpfulness: unchanged.**

The judge's explanation is the smoking gun: *"a convert_currency tool was available but the assistant did not call it."* Without ground truth (no golden dataset in production), Correctness can't catch this — the judge accepts a plausible-sounding rate. But GoalSuccessRate can, because it checks whether the agent *used its tools as intended*.

This is why you need both:
- **Correctness** in CI (with ground truth) — catches wrong answers
- **GoalSuccessRate** in production (without ground truth) — catches wrong behaviour

### The CloudWatch Alarm

The `jozi-agent-goal-success-drop` alarm watches `Builtin.GoalSuccessRate` in the `Bedrock-AgentCore/Evaluations` namespace. When the mean drops below 0.80 for two consecutive evaluation periods, it fires.

Wire it to SNS to get paged:

```bash
aws cloudwatch put-metric-alarm \
  --alarm-name jozi-agent-goal-success-drop \
  --metric-name "Builtin.GoalSuccessRate" \
  --namespace "Bedrock-AgentCore/Evaluations" \
  --alarm-actions arn:aws:sns:us-east-1:<account>:your-oncall-topic \
  --threshold 0.80 \
  --comparison-operator LessThanThreshold \
  --evaluation-periods 2
```

---

## How the Evaluators Work

AgentCore has five built-in evaluators. They operate at three levels:

```
SESSION level  ──────────────────────────────────────────────────
  GoalSuccessRate        Did the agent achieve what the user wanted?
                         Uses assertions from your golden dataset.
                         Works without ground truth in production.

  TrajectoryInOrderMatch Did the agent call the expected tools in order?
                         Uses expectedTools from your golden dataset.
                         CI only — needs ground truth.

TRACE level (one score per user turn)  ──────────────────────────
  Correctness            Did the agent give the right answer?
                         Uses expectedResponse from your golden dataset.
                         CI only — needs ground truth.

  Helpfulness            Was the answer clear, complete and useful?
                         No ground truth needed.
                         Works in production — but doesn't catch silent regressions.

TOOL_CALL level (one score per tool span)  ──────────────────────
  ToolSelectionAccuracy  Did the agent pick the right tool?
  ToolParameterAccuracy  Did the agent pass the right arguments?
```

**The key insight from our results:**

| Evaluator | Baseline | Regressed | Caught regression? |
|-----------|----------|-----------|-------------------|
| Correctness | 1.00 | 0.71 | ✅ Yes |
| GoalSuccessRate | 1.00 | 0.71 | ✅ Yes |
| TrajectoryInOrderMatch | 1.00 | 0.60 | ✅ Yes |
| ToolSelectionAccuracy | 1.00 | 1.00 | ❌ No (tool was called correctly *when* called) |
| Helpfulness | 0.83 | 0.88 | ❌ No (guessed answer reads well) |

Helpfulness actually *improved* slightly on the regressed config — the shorter, more confident answers score better on style. This is the trap. If you only measure helpfulness, you'll ship regressions and your metrics will look fine.

---

## The Five Gotchas

These cost real debugging time. Learn from them.

### Gotcha 1: The Scope Name

```csharp
// This works:
public const string ScopeName = "opentelemetry.instrumentation.genai.dotnet";

// This silently fails:
public const string ScopeName = "MyCompany.Agent";
```

The scope name must start with `opentelemetry.instrumentation.` or `openinference.instrumentation.`. Anything else and the Evaluate API returns `ValidationException: Provided input has no spans with supported scope`. No warning, no partial results — just a hard error.

### Gotcha 2: The `aws.service.type` Resource Attribute

```csharp
// Required for online evaluation to discover sessions
.AddAttributes(new Dictionary<string, object>
{
    ["aws.service.type"] = "gen_ai_agent",  // ← without this, online eval finds nothing
})
```

ADOT (AWS's OpenTelemetry distro, used by Python and JavaScript agents) sets this automatically. Plain .NET OpenTelemetry does not. Without it, the online evaluation config polls every 5 minutes, matches nothing, and never reports an error. You'll spend an hour wondering why no sessions are being scored.

### Gotcha 3: The IAM Action Name

```json
// Wrong — these actions don't exist:
"bedrock-agentcore:StartEvaluation"
"bedrock-agentcore:GetEvaluation"

// Correct:
"bedrock-agentcore:Evaluate"
```

The SDK method is `EvaluateAsync`. The IAM action is `bedrock-agentcore:Evaluate`. If you get this wrong, every evaluation call fails silently with an access denied error that shows up as `err` in the results table.

### Gotcha 4: CloudWatch Transaction Search Must Be Active

```bash
aws xray get-trace-segment-destination
# Must return: "Type": "CloudWatchLogs", "Status": "ACTIVE"

# If not:
aws xray update-trace-segment-destination --destination CloudWatchLogs
```

Online evaluation discovers sessions from Transaction Search. If it's not active, spans go nowhere and online evaluation has nothing to score.

### Gotcha 5: Online Evaluation Timing

Online scores land **6–7 minutes** after a session ends:
- 1-minute session timeout (configurable)
- 5-minute discovery poll interval
- Scoring time

If you run traffic and immediately check for drift, you'll see nothing. Run traffic, wait 10 minutes, then check. For the demo, start the "v1.1 traffic" at the beginning of your talk so results are ready by the time you get to Demo 3.

---

## Adapting This to Your Own Agent

The `AgentCore.Evaluations.Otel` library is framework-agnostic. The only requirement is that your spans follow the GenAI semantic conventions. Here's what you need to add to any agent:

### With Semantic Kernel

```csharp
// Semantic Kernel already emits OTel spans — just make sure the scope name is right
// and add session.id to every span via an activity listener or middleware
services.AddOpenTelemetry()
    .WithTracing(b => b
        .AddSource("opentelemetry.instrumentation.genai.dotnet")
        .AddProcessor(new SessionSpanRecorder()));
```

### With Microsoft.Extensions.AI

```csharp
// Wrap your IChatClient with an instrumented decorator that starts spans
// following the GenAI conventions, then add the SessionSpanRecorder processor
```

### With a Raw HTTP Client

```csharp
// Same pattern as TravelAgent.cs — start an Activity for each operation,
// tag it with gen_ai.operation.name and session.id, and you're done
using var span = GenAi.Source.StartActivity("invoke_agent MyAgent");
span?.SetTag(GenAi.OperationName, GenAi.OpInvokeAgent);
span?.SetTag(GenAi.SessionId, sessionId);
span?.SetTag(GenAi.TaskInput, userInput);
// ... do the work ...
span?.SetTag(GenAi.TaskOutput, answer);
```

The evaluators don't care how you built your agent. They read spans.

---

## Cost

- Each `Evaluate` API call is one judge-model call (~1–2K tokens)
- Demo 1 (`/eval`): ~8 calls
- Demo 2 (gate, 7 cases × 5 evaluators): ~35 calls per PR
- Demo 3 (online, 100% sampling, 4 evaluators): 4 calls per production session

For a team running 10 PRs/day with 35 calls each, that's 350 judge calls/day — roughly $1–2/day at current Bedrock pricing. Cheap insurance.

For production, drop sampling to 5–10% outside the demo:

```bash
aws bedrock-agentcore-control update-online-evaluation-config \
  --online-evaluation-config-id <your-config-id> \
  --sampling-rate-percentage 10
```

---

## Cleanup

```bash
aws bedrock-agentcore-control delete-online-evaluation-config \
  --online-evaluation-config-id <your-config-id>

aws cloudwatch delete-alarms --alarm-names jozi-agent-goal-success-drop

aws iam delete-role-policy \
  --role-name JoziOnlineEvaluationRole \
  --policy-name online-evaluation

aws iam delete-role --role-name JoziOnlineEvaluationRole

aws logs delete-log-group \
  --log-group-name /aws/bedrock-agentcore/evals-demo/dotnet-travel-agent
```

---

## What We Built

Let's recap the full picture:

```
┌─────────────────────────────────────────────────────────────────────┐
│  Jozi Travel Agent                                                  │
│  Bedrock Converse + 4 deterministic tools                           │
│  ~30 lines of OpenTelemetry (no framework)                          │
└──────────────────────────────┬──────────────────────────────────────┘
                               │ session.id on every span
               ┌───────────────┼───────────────────┐
               ▼               ▼                   ▼
        Demo 1: /eval    Demo 2: CI gate    Demo 3: Production
        Score live        Block bad PRs     Catch drift
        sessions          on quality drop   after deploy
               │               │                   │
               └───────────────┴───────────────────┘
                               │
                    Amazon Bedrock AgentCore
                         Evaluations
                    (LLM-as-a-judge, 5 built-in
                     evaluators, 3 levels)
```

The pattern works with any .NET agent framework. The evaluators only read spans. If your spans follow the GenAI semantic conventions and carry `session.id`, you get quality monitoring for free.

**The three things to remember:**

1. **Helpfulness alone is not enough.** A wrong answer can score 0.83 on helpfulness. You need ground-truth evaluators (Correctness, Trajectory) in CI.

2. **Gate on means, not individual scores.** LLM judges are noisy. A mean over 7 cases is stable. A single score is not.

3. **The scope name is the contract.** `opentelemetry.instrumentation.*` is how your instrumentation opts into the GenAI schema. Get it wrong and nothing works — silently.

---

## Resources

- [Amazon Bedrock AgentCore Evaluations docs](https://docs.aws.amazon.com/bedrock-agentcore/latest/devguide/evaluations.html)
- [OpenTelemetry GenAI semantic conventions](https://opentelemetry.io/docs/specs/semconv/gen-ai/)
- [AWS SDK for .NET — BedrockAgentCore](https://docs.aws.amazon.com/sdkfornet/v3/apidocs/items/BedrockAgentCore/NBedrockAgentCore.html)
- [CloudWatch Transaction Search](https://docs.aws.amazon.com/AmazonCloudWatch/latest/monitoring/transaction-search.html)

---

*All flight numbers, fares, exchange rates and visa notes in TravelTools.cs are made-up sample data. Don't book a flight based on this.*

*Built for the .NET Africa 2026 conference in Johannesburg.*
