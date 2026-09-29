#!/usr/bin/env bash
# Open black-box check of the current assignment (week 3). The checker itself
# lives in autocheck/ and is the pinned week-3 public checker, vendored
# verbatim; the week-2 one it replaced is archived under oldcheck/week2 and is
# still runnable from there.
#
# autocheck/check.sh takes --repo as a required argument, because upstream it
# is run from its own clone against a candidate repository. Here the candidate
# is this repository, so this wrapper supplies it -- an explicit --repo still
# wins, which is what makes the two invocations identical.
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"

for argument in "$@"; do
  case "$argument" in
    --repo|--repo=*)
      exec "$script_dir/autocheck/check.sh" "$@"
      ;;
  esac
done

exec "$script_dir/autocheck/check.sh" --repo "$script_dir" "$@"
