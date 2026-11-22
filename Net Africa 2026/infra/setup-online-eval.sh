#!/usr/bin/env bash
# DEMO 3 setup — continuous quality monitoring of production traffic.
#
# Creates (idempotently):
#   1. the CloudWatch log group the agent writes spans to
#   2. an IAM execution role that AgentCore Evaluations assumes to read spans and write results
#   3. an online evaluation config: 100% sampling, built-in evaluators, 1-minute session timeout (demo-friendly)
#   4. a CloudWatch alarm on the Correctness score
#
# Usage:  infra/setup-online-eval.sh
# Env overrides: AWS_REGION, SERVICE_NAME, LOG_GROUP, ROLE_NAME, CONFIG_NAME, SAMPLING, SESSION_TIMEOUT_MINUTES
#
# Production note: start with 5-10% sampling. Every sampled session costs one judge-model call per evaluator.

set -euo pipefail

REGION="${AWS_REGION:-us-east-1}"
ACCOUNT="$(aws sts get-caller-identity --query Account --output text)"
SERVICE_NAME="${SERVICE_NAME:-dotnet-travel-agent}"
LOG_GROUP="${LOG_GROUP:-/aws/bedrock-agentcore/evals-demo/dotnet-travel-agent}"
ROLE_NAME="${ROLE_NAME:-JoziOnlineEvaluationRole}"
CONFIG_NAME="${CONFIG_NAME:-jozi_travel_agent_online}"
SAMPLING="${SAMPLING:-100}"
SESSION_TIMEOUT_MINUTES="${SESSION_TIMEOUT_MINUTES:-1}"
HERE="$(cd "$(dirname "$0")" && pwd)"

echo "Account $ACCOUNT · region $REGION · service.name $SERVICE_NAME"

# 1. Log group for spans -------------------------------------------------------------------------
aws logs create-log-group --region "$REGION" --log-group-name "$LOG_GROUP" 2>/dev/null && echo "Created log group $LOG_GROUP" || echo "Log group $LOG_GROUP exists"
aws logs put-retention-policy --region "$REGION" --log-group-name "$LOG_GROUP" --retention-in-days 14

# 2. Execution role ------------------------------------------------------------------------------
TRUST=$(sed -e "s/{{ACCOUNT}}/$ACCOUNT/g" -e "s/{{REGION}}/$REGION/g" "$HERE/online-eval-role-trust.json")
POLICY=$(sed -e "s/{{ACCOUNT}}/$ACCOUNT/g" -e "s/{{REGION}}/$REGION/g" -e "s#{{LOG_GROUP}}#$LOG_GROUP#g" "$HERE/online-eval-role-policy.json")

if aws iam get-role --role-name "$ROLE_NAME" >/dev/null 2>&1; then
  echo "Role $ROLE_NAME exists"
else
  aws iam create-role --role-name "$ROLE_NAME" --assume-role-policy-document "$TRUST" \
    --description "AgentCore Evaluations online evaluation for the Jozi .NET demo agent" >/dev/null
  echo "Created role $ROLE_NAME"
fi
aws iam put-role-policy --role-name "$ROLE_NAME" --policy-name online-evaluation --policy-document "$POLICY"
ROLE_ARN="arn:aws:iam::$ACCOUNT:role/$ROLE_NAME"
echo "Waiting for IAM propagation…"; sleep 10

# 3. Online evaluation config --------------------------------------------------------------------
EXISTING=$(aws bedrock-agentcore-control list-online-evaluation-configs --region "$REGION" \
  --query "onlineEvaluationConfigs[?onlineEvaluationConfigName=='$CONFIG_NAME'].onlineEvaluationConfigId | [0]" --output text 2>/dev/null || echo "None")

if [[ "$EXISTING" != "None" && -n "$EXISTING" ]]; then
  CONFIG_ID="$EXISTING"
  echo "Online evaluation config $CONFIG_NAME exists ($CONFIG_ID)"
else
  CONFIG_ID=$(aws bedrock-agentcore-control create-online-evaluation-config --region "$REGION" \
    --online-evaluation-config-name "$CONFIG_NAME" \
    --description "Continuous quality monitoring for the Jozi .NET travel agent" \
    --rule "{\"samplingConfig\":{\"samplingPercentage\":$SAMPLING},\"sessionConfig\":{\"sessionTimeoutMinutes\":$SESSION_TIMEOUT_MINUTES}}" \
    --data-source-config "{\"cloudWatchLogs\":{\"logGroupNames\":[\"$LOG_GROUP\"],\"serviceNames\":[\"$SERVICE_NAME\"]}}" \
    --evaluators '[{"evaluatorId":"Builtin.Correctness"},{"evaluatorId":"Builtin.Helpfulness"},{"evaluatorId":"Builtin.GoalSuccessRate"},{"evaluatorId":"Builtin.ToolSelectionAccuracy"}]' \
    --evaluation-execution-role-arn "$ROLE_ARN" \
    --enable-on-create \
    --query onlineEvaluationConfigId --output text)
  echo "Created online evaluation config $CONFIG_NAME ($CONFIG_ID)"
fi

RESULTS_LOG_GROUP=$(aws bedrock-agentcore-control get-online-evaluation-config --region "$REGION" \
  --online-evaluation-config-id "$CONFIG_ID" --query outputConfig.cloudWatchConfig.logGroupName --output text)

# 4. Alarm on quality (GoalSuccessRate: in testing it was the online signal that dropped when the agent
#    stopped using tools; Correctness without ground truth accepted the guessed answers) ----------------------------------------------------------------------------
# Scores are published as metrics in Bedrock-AgentCore/Evaluations, metric name = evaluator id. Online scores carry
# onlineEvaluationConfigId + service.name; alarming on both keeps batch-job scores out of the production signal.
aws cloudwatch put-metric-alarm --region "$REGION" \
  --alarm-name "jozi-agent-goal-success-drop" \
  --alarm-description "Average GoalSuccessRate of sampled production sessions fell below 0.8" \
  --namespace "Bedrock-AgentCore/Evaluations" \
  --metric-name "Builtin.GoalSuccessRate" \
  --dimensions "Name=onlineEvaluationConfigId,Value=$CONFIG_ID" "Name=service.name,Value=$SERVICE_NAME" \
  --statistic Average --period 300 --evaluation-periods 1 \
  --threshold 0.8 --comparison-operator LessThanThreshold \
  --treat-missing-data notBreaching
echo "Alarm jozi-agent-goal-success-drop configured (add --alarm-actions <sns-topic-arn> to get paged)."

cat <<EOF

Done.
  Spans log group          $LOG_GROUP
  Online eval config id    $CONFIG_ID
  Results log group        $RESULTS_LOG_GROUP

Next:
  dotnet run --project src/Jozi.TravelAgent.Evals -- traffic --sessions 10
  dotnet run --project src/Jozi.TravelAgent.Evals -- drift --results-log-group "$RESULTS_LOG_GROUP"
EOF
