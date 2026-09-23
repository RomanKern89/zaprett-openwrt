#!/bin/bash
# Free disk space on a GitHub-hosted runner before an SDK build (the SDK image is ~3.2 GB, a build ~2 GB more).
# Removes preinstalled toolchains this project never uses. Refuses to run anywhere but on GitHub Actions.
set -euo pipefail
[ "${GITHUB_ACTIONS:-}" = true ] || { echo "free-disk.sh: only for GitHub-hosted runners" >&2; exit 2; }
df -h / | tail -n 1 | awk '{ print "before: " $4 " free of " $2 }'
sudo rm -rf /usr/share/dotnet /usr/local/lib/android /opt/ghc /usr/local/.ghcup /opt/hostedtoolcache/CodeQL \
	/usr/local/share/boost /usr/share/swift
sudo docker image prune --all --force > /dev/null
df -h / | tail -n 1 | awk '{ print "after:  " $4 " free of " $2 }'
