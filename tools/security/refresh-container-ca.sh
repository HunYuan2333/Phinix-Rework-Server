#!/bin/sh
# Ubuntu USN-8944-1 / eMudhra incident, 2026-10-20. Only for the explicit Noble images.
set -eu
. /etc/os-release
test "$ID:$VERSION_ID" = "ubuntu:24.04"
apt-get update
DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends ca-certificates libssl3t64
installed=$(dpkg-query -W -f='${Version}' ca-certificates)
dpkg --compare-versions "$installed" ge '20260816ubuntu0.24.04.1'
update-ca-certificates
dpkg-query -W ca-certificates libssl3t64
rm -rf /var/lib/apt/lists/*
