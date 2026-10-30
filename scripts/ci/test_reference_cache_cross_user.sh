#!/usr/bin/env bash
# AC-62/63/65: only inside an explicitly disposable container. No host identities.
set -euo pipefail
[[ -f /.dockerenv && "${PPDS_DISPOSABLE_CACHE_FIXTURE:-}" == 1 && "$EUID" == 0 ]]
[[ ! -e /fixture ]]
apt-get update -qq
apt-get install -y -qq --no-install-recommends acl >/dev/null
mkdir -m 755 /fixture
mkdir -m 1777 /fixture/shared
touch /fixture/shared/.ppds-cache-probe
install -d -m 700 -o 65534 -g 65534 /fixture/victim
install -d -m 700 -o 33 -g 33 /fixture/attacker

as_victim() {
  setpriv --reuid 65534 --regid 65534 --clear-groups env HOME=/fixture/victim \
    XDG_DATA_HOME=/fixture/victim/data DOTNET_CLI_HOME=/fixture/victim \
    TMPDIR=/fixture/shared PPDS_CACHE_PROBE_ROOT=/fixture/shared "$@"
}
as_attacker() {
  setpriv --reuid 33 --regid 33 --clear-groups env HOME=/fixture/attacker \
    XDG_DATA_HOME=/fixture/attacker/data DOTNET_CLI_HOME=/fixture/attacker \
    TMPDIR=/fixture/shared PPDS_CACHE_PROBE_ROOT=/fixture/shared "$@"
}
victim() { as_victim dotnet /probe/ReferenceCacheProbe.dll "$@"; }
attacker() { as_attacker dotnet /probe/ReferenceCacheProbe.dll "$@"; }
must_deny() {
  if as_attacker "$@"; then
    echo "FAIL: second identity unexpectedly changed victim storage" >&2
    exit 1
  fi
}

CrossUserCacheBoundary() {
  local legacy cache inherited
  legacy=$(attacker seed-legacy nobody)
  find "$legacy" -type f -exec sha256sum {} + | sort > /fixture/legacy.before
  cache=$(victim create)
  [[ "$cache" == /fixture/victim/data/PPDS/cache/net462-ref/* ]]
  must_deny sh -c 'printf changed > "$1/System.Runtime.dll"' sh "$cache"
  must_deny rm "$cache/System.Runtime.dll"
  must_deny touch "$cache/Injected.dll"
  must_deny mv "$cache" "$cache-replaced"
  must_deny sh -c 'printf changed > "$1.lock"' sh "$cache"
  [[ "$(victim create)" == "$cache" ]]

  # Squatting an inaccessible legacy directory cannot disable the private cache.
  as_attacker chmod 000 /fixture/shared/ppds-nobody
  [[ "$(victim create)" == "$cache" ]]
  as_attacker chmod 755 /fixture/shared/ppds-nobody
  find "$legacy" -type f -exec sha256sum {} + | sort > /fixture/legacy.after
  cmp /fixture/legacy.before /fixture/legacy.after

  as_attacker mkdir /fixture/shared/foreign
  as_attacker chmod 755 /fixture/shared/foreign
  as_attacker sh -c 'printf preserve > /fixture/shared/foreign/marker'
  victim reject /fixture/shared/foreign
  [[ "$(cat /fixture/shared/foreign/marker)" == preserve ]]

  # A protected final destination must not hide an untrusted intermediate link.
  as_victim mkdir /fixture/victim/target
  as_attacker ln -s /fixture/victim/target /fixture/shared/foreign/hop
  as_victim ln -s /fixture/shared/foreign/hop /fixture/victim/link
  victim reject /fixture/victim/link/cache
  [[ ! -e /fixture/victim/target/cache ]]

  # Default ACL + permissive umask must not leak write access during or after staging.
  as_victim mkdir /fixture/victim/inherited
  as_victim chmod 755 /fixture/victim /fixture/victim/inherited
  as_victim setfacl -m d:u:33:rwx,d:g::r-x,d:o::r-x /fixture/victim/inherited
  inherited=$(as_victim sh -c 'umask 000; exec dotnet /probe/ReferenceCacheProbe.dll create /fixture/victim/inherited')
  [[ "$(stat -c %a "$inherited")" == 700 ]]
  [[ "$(stat -c %a "$inherited.lock")" == 600 ]]
  [[ -z "$(find "$inherited" -type f -perm /022 -print)" ]]
  must_deny sh -c 'printf changed > "$1/System.Runtime.dll"' sh "$inherited"
  [[ "$(victim create /fixture/victim/inherited)" == "$inherited" ]]

  # Owner and filesystem-object checks run before completeness reads or cleanup.
  chown 33:33 "$inherited/System.Runtime.dll"
  victim reject /fixture/victim/inherited
  chown 65534:65534 "$inherited/System.Runtime.dll"
  as_victim mkfifo "$inherited/unexpected.pipe"
  victim reject /fixture/victim/inherited
  as_victim rm "$inherited/unexpected.pipe"

  as_victim rm "$cache/System.Runtime.dll"
  local pids=()
  for index in 1 2 3 4; do victim create > "/fixture/result-$index" & pids+=("$!"); done
  for pid in "${pids[@]}"; do wait "$pid"; done
  for index in 1 2 3 4; do [[ "$(cat "/fixture/result-$index")" == "$cache" ]]; done
  echo "PASS AC-62/63/65: cross-user isolation, legacy squat, foreign owner, link chain, default ACL, FIFO, concurrent processes"
}
CrossUserCacheBoundary
