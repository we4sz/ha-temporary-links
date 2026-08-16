#!/usr/bin/env bash
set -euo pipefail

# Runs the HA integration suite against a throwaway REAL Home Assistant in Docker.
# Opt-in and local-only: nothing in the default `dotnet test` gate needs Docker.
#
# Usage: tests/integration/run-ha-tests.sh [extra dotnet-test args...]
#   HA_TEST_IMAGE  override the HA image (default: ghcr.io/home-assistant/home-assistant:stable)
#   HA_TEST_PORT   host port for the container (default: 18123)
#
# To run against an existing HA instead of a container:
#   HA_TEST_URL=http://homeassistant.local:8123 HA_TEST_TOKEN=<long-lived token> \
#     dotnet test tests/TemporaryLinks.Addon.IntegrationTests

REPO_ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
IMAGE="${HA_TEST_IMAGE:-ghcr.io/home-assistant/home-assistant:stable}"
PORT="${HA_TEST_PORT:-18123}"
NAME="ha-temporary-links-it"

cleanup() {
  docker rm -f "$NAME" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker rm -f "$NAME" >/dev/null 2>&1 || true
echo "Starting $IMAGE on port $PORT (fresh instance, config stays in the container)..."
docker run -d --name "$NAME" -p "$PORT:8123" "$IMAGE" >/dev/null

echo -n "Waiting for Home Assistant to come up"
up=""
for _ in $(seq 1 120); do
  if curl -fsS -o /dev/null "http://localhost:$PORT/manifest.json"; then
    up=1
    break
  fi
  echo -n "."
  sleep 2
done
echo
if [ -z "$up" ]; then
  echo "Home Assistant never came up; container logs:" >&2
  docker logs "$NAME" >&2
  exit 1
fi

HA_TEST_URL="http://localhost:$PORT" \
  dotnet test "$REPO_ROOT/tests/TemporaryLinks.Addon.IntegrationTests" "$@"
