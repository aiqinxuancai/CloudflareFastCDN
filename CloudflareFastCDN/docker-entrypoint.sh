#!/bin/sh
set -eu

if [ -n "${TZ:-}" ] && [ -f "/usr/share/zoneinfo/$TZ" ]; then
    ln -snf "/usr/share/zoneinfo/$TZ" /etc/localtime
    echo "$TZ" > /etc/timezone
fi

exec dotnet CloudflareFastCDN.dll "$@"
