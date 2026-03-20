#!/usr/bin/env bash
# renew-certs.sh — renew Let's Encrypt certificates via webroot method.
#
# The full stack (nginx + app) must already be running because nginx serves
# the ACME webroot challenge at /.well-known/acme-challenge/.
#
# Add to crontab to run twice daily (Let's Encrypt recommends this):
#   0 0,12 * * * /path/to/liftoff/Server/renew-certs.sh >> /var/log/liftoff-renew.log 2>&1

set -euo pipefail

cd "$(dirname "$0")"

docker run --rm \
  -v "$(pwd)/certbot/conf:/etc/letsencrypt" \
  -v "$(pwd)/certbot/www:/var/www/certbot" \
  certbot/certbot renew \
    --webroot \
    --webroot-path=/var/www/certbot \
    --quiet

# Reload nginx so it picks up any newly renewed certificate
docker compose exec nginx nginx -s reload
