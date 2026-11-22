#!/usr/bin/env bash
# DEMO 2 setup — lets GitHub Actions call Bedrock and AgentCore Evaluations with OIDC (no stored AWS keys).
#
# Usage: infra/setup-github-oidc.sh <owner/repo>
# Then:  gh variable set AWS_EVAL_ROLE_ARN --body <printed arn> --repo <owner/repo>

set -euo pipefail
REPO="${1:?usage: setup-github-oidc.sh <owner/repo>}"
REGION="${AWS_REGION:-us-east-1}"
ROLE_NAME="${ROLE_NAME:-JoziAgentQualityGate}"
ACCOUNT="$(aws sts get-caller-identity --query Account --output text)"
PROVIDER_ARN="arn:aws:iam::$ACCOUNT:oidc-provider/token.actions.githubusercontent.com"

if ! aws iam get-open-id-connect-provider --open-id-connect-provider-arn "$PROVIDER_ARN" >/dev/null 2>&1; then
  aws iam create-open-id-connect-provider --url https://token.actions.githubusercontent.com \
    --client-id-list sts.amazonaws.com >/dev/null
  echo "Created GitHub OIDC provider"
fi

TRUST=$(cat <<EOF
{
  "Version": "2012-10-17",
  "Statement": [{
    "Effect": "Allow",
    "Principal": { "Federated": "$PROVIDER_ARN" },
    "Action": "sts:AssumeRoleWithWebIdentity",
    "Condition": {
      "StringEquals": { "token.actions.githubusercontent.com:aud": "sts.amazonaws.com" },
      "StringLike":   { "token.actions.githubusercontent.com:sub": "repo:$REPO:*" }
    }
  }]
}
EOF
)

POLICY=$(cat <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Sid": "RunTheAgent",
      "Effect": "Allow",
      "Action": ["bedrock:InvokeModel", "bedrock:InvokeModelWithResponseStream"],
      "Resource": [
        "arn:aws:bedrock:*::foundation-model/anthropic.claude-*",
        "arn:aws:bedrock:*:$ACCOUNT:inference-profile/us.anthropic.claude-*"
      ]
    },
    {
      "Sid": "ScoreWithBuiltInEvaluators",
      "Effect": "Allow",
      "Action": ["bedrock-agentcore:Evaluate"],
      "Resource": "*"
    },
    {
      "Sid": "DriftCheckReadsResults",
      "Effect": "Allow",
      "Action": ["logs:StartQuery", "logs:GetQueryResults"],
      "Resource": "arn:aws:logs:$REGION:$ACCOUNT:log-group:/aws/bedrock-agentcore/evaluations/*"
    }
  ]
}
EOF
)

if aws iam get-role --role-name "$ROLE_NAME" >/dev/null 2>&1; then
  aws iam update-assume-role-policy --role-name "$ROLE_NAME" --policy-document "$TRUST"
else
  aws iam create-role --role-name "$ROLE_NAME" --assume-role-policy-document "$TRUST" \
    --description "GitHub Actions agent quality gate for $REPO" >/dev/null
fi
aws iam put-role-policy --role-name "$ROLE_NAME" --policy-name agent-quality-gate --policy-document "$POLICY"

echo "Role: arn:aws:iam::$ACCOUNT:role/$ROLE_NAME"
echo "Run:  gh variable set AWS_EVAL_ROLE_ARN --body arn:aws:iam::$ACCOUNT:role/$ROLE_NAME --repo $REPO"
