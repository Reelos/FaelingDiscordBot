#!/usr/bin/env bash
# Stops a running container (if exists), builds a new image, and starts a container with the same name
# Usage: ./build-and-restart.sh <image-name> <container-name> [path-to-dockerfile-dir]

set -euo pipefail

IMAGE_NAME=${1:-faeling-discord-bot}
CONTAINER_NAME=${2:-faeling-discord-bot}
BUILD_PATH=${3:-..}
SECRETS_FILE="$(pwd)/secrets.env"

if [ ! -f "$SECRETS_FILE" ]; then
  echo "Secrets file not found: $SECRETS_FILE"
  echo "Copy docker/secrets.example.env to docker/secrets.env and fill in values." >&2
  exit 1
fi

# Stop existing container if running
if docker ps --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
  echo "Stopping running container ${CONTAINER_NAME}..."
  docker stop "${CONTAINER_NAME}"
fi

# Remove existing container if present
if docker ps -a --format '{{.Names}}' | grep -q "^${CONTAINER_NAME}$"; then
  echo "Removing existing container ${CONTAINER_NAME}..."
  docker rm "${CONTAINER_NAME}"
fi

# Build image
echo "Building image ${IMAGE_NAME} from ${BUILD_PATH}..."
docker build -t "${IMAGE_NAME}" "${BUILD_PATH}"

# Run container
echo "Starting container ${CONTAINER_NAME}..."
docker run -d --name "${CONTAINER_NAME}" --env-file "$SECRETS_FILE" "${IMAGE_NAME}"

echo "Done."
