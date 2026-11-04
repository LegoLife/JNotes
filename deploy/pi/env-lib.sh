#!/bin/sh
# set_env KEY VALUE — replaces or adds KEY=VALUE in .env (kept readable only by you).
set_env() {
  touch .env
  chmod 600 .env
  grep -v "^$1=" .env > .env.tmp || true
  printf '%s=%s\n' "$1" "$2" >> .env.tmp
  mv .env.tmp .env
  chmod 600 .env
}
