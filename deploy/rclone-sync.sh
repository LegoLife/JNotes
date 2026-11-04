#!/bin/sh
# Copies JNotes backups and images to Google Drive every hour (runs in the rclone container).
#   database/: nightly DB snapshots, keeping the last 7 days on Drive
#   images/:   uploaded images (they never change, so each is copied once and never deleted)
# After a successful run it writes /data/backups/.last-drive-sync, which JNotes shows on its Settings page.
REMOTE="${RCLONE_REMOTE:-gdrive}:${RCLONE_FOLDER:-JNotes Backups}"

while true; do
  if rclone copy /data/backups "$REMOTE/database" --include "jnotes-*.db" --max-age 8d \
     && rclone delete "$REMOTE/database" --include "jnotes-*.db" --min-age 7d \
     && rclone copy /data/attachments "$REMOTE/images"; then
    date -u +%Y-%m-%dT%H:%M:%SZ > /data/backups/.last-drive-sync
    echo "$(date -u +%FT%TZ) copied to $REMOTE"
  else
    echo "$(date -u +%FT%TZ) Drive copy failed; retrying in an hour" >&2
  fi
  [ -n "$RUN_ONCE" ] && exit 0
  sleep 3600
done
