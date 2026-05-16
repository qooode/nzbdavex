#!/bin/sh

# Use env vars or default to 1000
PUID=${PUID:-1000}
PGID=${PGID:-1000}

# Create group if it doesn't exist
if ! getent group appgroup >/dev/null; then
    addgroup -g "$PGID" appgroup
fi

# Create user if it doesn't exist
if ! id appuser >/dev/null 2>&1; then
    adduser -D -H -u "$PUID" -G appgroup appuser
fi

# Switch to new user and run app
exec su-exec appuser ./NzbWebDAV