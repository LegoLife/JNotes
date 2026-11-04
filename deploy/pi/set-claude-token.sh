#!/bin/sh
# Stores the Claude Code sign-in token that lets JNotes answer /ai requests with your Claude subscription.
# Create the token on a computer with Claude Code by running:  claude setup-token
set -e
cd "$(dirname "$0")"
. ./env-lib.sh

printf "Paste the token from 'claude setup-token': "; stty -echo; read -r TOKEN; stty echo; echo
TOKEN=$(printf '%s' "$TOKEN" | tr -d '[:space:]')
# Input is hidden, so pasting twice is easy: keep one copy if the token is repeated.
HALF=$(( ${#TOKEN} / 2 ))
if [ $(( ${#TOKEN} % 2 )) -eq 0 ] && [ "$(printf '%s' "$TOKEN" | cut -c1-$HALF)" = "$(printf '%s' "$TOKEN" | cut -c$((HALF + 1))-)" ]; then
  TOKEN=$(printf '%s' "$TOKEN" | cut -c1-$HALF)
  echo "(It looks like the token was pasted twice; using one copy.)"
fi
case "$TOKEN" in
  sk-ant-*) ;;
  *) echo "That doesn't look like a Claude token (it should start with sk-ant-)."; exit 1 ;;
esac
set_env CLAUDE_CODE_OAUTH_TOKEN "$TOKEN"
docker compose up -d jnotes
echo "Saved. In JNotes, open the ✨ Claude page and press 'Test it'."
