#!/usr/bin/env bash
# Logs docker in to the ECR registry that holds one environment's images.
#
# Usage: scripts/ecr_login.sh [dev|stg|prd]   (default dev)
# Env:   AWS_PROFILE  SSO profile with ECR read access in that account
#                     (default song88-plat-stg for dev/stg, song88-plat-prd for prd;
#                     the README shows the ~/.aws/config entry)
#
# Uses the aws CLI when it is installed, otherwise the amazon/aws-cli image with ~/.aws mounted.
# The docker login lasts 12 hours; the SSO session it starts lasts 2.
set -euo pipefail

env="${1:-dev}"
case "$env" in
    dev|stg) registry=172065883315.dkr.ecr.ap-south-1.amazonaws.com; region=ap-south-1
             profile="${AWS_PROFILE:-song88-plat-stg}" ;;
    prd)     registry=525375333516.dkr.ecr.ap-east-1.amazonaws.com; region=ap-east-1
             profile="${AWS_PROFILE:-song88-plat-prd}" ;;
    *) echo "Usage: $0 [dev|stg|prd]" >&2; exit 2 ;;
esac

aws_cli() {
    if command -v aws >/dev/null; then
        aws "$@"
    else
        docker run --rm -i -v "$HOME/.aws:/root/.aws" amazon/aws-cli:2.27.0 "$@"
    fi
}

# Reuse a live SSO session; otherwise start one (it prints a URL and a code to approve).
if ! aws_cli sts get-caller-identity --profile "$profile" >/dev/null 2>&1; then
    aws_cli sso login --profile "$profile" --use-device-code
fi
aws_cli ecr get-login-password --profile "$profile" --region "$region" \
    | docker login --username AWS --password-stdin "$registry"
