#!/usr/bin/env bash
# init-certs.sh — obtain the initial Let's Encrypt certificate.
#
# Run this ONCE on the Ubuntu host before starting docker compose for the
# first time.  After certs are in place, docker compose up -d handles
# everything, including renewal via the certbot container.
#
# Usage:
#   chmod +x init-certs.sh
#   ./init-certs.sh yourdomain.com you@example.com

set -euo pipefail

DOMAIN=${1:?"Usage: $0 <domain> <email>"}
EMAIL=${2:?"Usage: $0 <domain> <email>"}

echo ">>> Creating certbot directories..."
mkdir -p certbot/conf certbot/www

echo ">>> Obtaining certificate for ${DOMAIN} (standalone mode — port 80 must be free)..."
docker run --rm \
  -p 80:80 \
  -v "$(pwd)/certbot/conf:/etc/letsencrypt" \
  -v "$(pwd)/certbot/www:/var/www/certbot" \
  certbot/certbot certonly \
    --standalone \
    --email "${EMAIL}" \
    --agree-tos \
    --no-eff-email \
    -d "${DOMAIN}"

echo ""
echo ">>> Certificate obtained."
echo ">>> Replace YOUR_DOMAIN in nginx/nginx.conf with: ${DOMAIN}"
echo ">>> Then start the stack: docker compose up -d"
