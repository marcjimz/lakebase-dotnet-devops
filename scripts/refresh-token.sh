#!/usr/bin/env bash
# The Lakebase SCM extension wraps commands in this script to refresh database tokens.
# This repo's app and CLI mint a short-lived Lakebase credential at runtime, so there is
# nothing to refresh: run the wrapped command as-is.
set -euo pipefail
exec "$@"
