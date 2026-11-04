#!/usr/bin/env bash
# Build the image, apply migrations, start the app, then wait for /health.
# Usage (on the server): sudo bash ~/identity/deploy/ec2/start.sh
set -euo pipefail

cd "$(dirname "$0")"
REGION=ap-south-1
SHARED_NETWORK=ec2_default

if ! docker network inspect "$SHARED_NETWORK" >/dev/null 2>&1; then
  echo "ERROR: Docker network $SHARED_NETWORK not found. Is the Postgres container (ec2-postgres-1) running?" >&2
  exit 1
fi

# The migrate container needs the database password; the instance role reads it from the same
# secret the app uses, so it is never written to a file on this server.
echo ">> Reading the database password from Secrets Manager"
IDENTITY_DB_PASSWORD="$(aws secretsmanager get-secret-value --secret-id routego/identity/db --region "$REGION" \
  --query SecretString --output text | python3 -c 'import json, sys; print(json.load(sys.stdin)["password"])')"
export IDENTITY_DB_PASSWORD

echo ">> Building image (first time takes 5-10 minutes)"
docker build -t routego/identity:latest ../..

echo ">> Applying migrations and starting the app"
if ! docker compose up -d; then
  echo "ERROR: migrations or app start failed. Last logs:" >&2
  docker compose ps -a
  docker compose logs --tail 50 migrate identity
  exit 1
fi

# /health, not /health/ready: readiness also needs an active signing key, which is set up after
# the first deploy with the rotate-key command.
echo ">> Waiting for the app to be healthy"
for _ in $(seq 1 30); do
  if curl -fs http://localhost:5001/health >/dev/null 2>&1; then
    docker compose ps -a
    echo ">> DEPLOY OK — app is healthy on port 5001"
    exit 0
  fi
  sleep 2
done

echo "ERROR: app did not become healthy. Last logs:" >&2
docker compose ps -a
docker compose logs --tail 50 migrate identity
exit 1
