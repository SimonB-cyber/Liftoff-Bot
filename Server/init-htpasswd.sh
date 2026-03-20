#!/usr/bin/env bash
# init-htpasswd.sh — create the admin Basic Auth password file.
#
# Run this once before starting docker compose.
# Uses Docker so apache2-utils doesn't need to be installed on the host.
#
# Usage:
#   chmod +x init-htpasswd.sh
#   ./init-htpasswd.sh <username>

set -euo pipefail

USERNAME=${1:?"Usage: $0 <username>"}

# Mount the nginx directory so htpasswd writes the file directly,
# and use -it so the password prompt works interactively.
docker run --rm -it \
  -v "$(pwd)/nginx:/mnt" \
  httpd:alpine \
  htpasswd -c /mnt/.htpasswd "${USERNAME}"

echo ">>> nginx/.htpasswd created for user '${USERNAME}'."
