#!/bin/sh
# Sets (or changes) the JNotes login password. The password is typed here on the Pi; only its hash is stored in .env.
set -e
cd "$(dirname "$0")"
. ./env-lib.sh

printf "New JNotes password: "; stty -echo; read -r PW; stty echo; echo
printf "Type it again: "; stty -echo; read -r PW2; stty echo; echo
[ "$PW" = "$PW2" ] || { echo "The passwords don't match."; exit 1; }
[ ${#PW} -ge 8 ] || { echo "Please use at least 8 characters."; exit 1; }

HASH=$(printf '%s\n' "$PW" | docker run --rm -i jnotes:latest hash-password 2>/dev/null | tail -n 1)
[ -n "$HASH" ] || { echo "Couldn't create the password hash."; exit 1; }
set_env JNOTES_PASSWORD_HASH "$HASH"
docker compose up -d jnotes
echo "Password set. Open http://$(hostname -I | cut -d' ' -f1):8080"
