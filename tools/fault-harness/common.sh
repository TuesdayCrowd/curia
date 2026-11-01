# Sourced by the harness scripts; not run directly.
#
# Configuration is environment only:
#   FH_DB          required by fault.sh and proxy.sh (and the read-only scripts): the throwaway database
#   FH_ROLE        required by fault.sh and proxy.sh: the Forum's application role in that database
#   FH_DIR         state directory (mode file, pid, logs, marks); default ${TMPDIR:-/tmp}/curia-fault-harness-<FH_DB>
#   FH_PROXY_PORT  proxy listen port, default 5433
#   FH_PG_HOST     Postgres host the proxy forwards to, default 127.0.0.1
#   FH_PG_PORT     Postgres port the proxy forwards to, default 5432
#   FH_FORUMLOG    the Forum's console log, read by mark.sh and census.sh (optional)
# psql runs as the admin connection from the standard PG* variables (PGHOST, PGPORT, PGUSER, PGPASSWORD); never through the proxy.
FH_SRC=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
FH_DB=${FH_DB:-}
FH_ROLE=${FH_ROLE:-}
FH_PROXY_PORT=${FH_PROXY_PORT:-5433}
FH_PG_HOST=${FH_PG_HOST:-127.0.0.1}
FH_PG_PORT=${FH_PG_PORT:-5432}
FH_FORUMLOG=${FH_FORUMLOG:-}
FH_DIR=${FH_DIR:-${TMPDIR:-/tmp}/curia-fault-harness-${FH_DB:-unset}}
export FH_DB FH_ROLE FH_DIR FH_PROXY_PORT FH_PG_HOST FH_PG_PORT FH_FORUMLOG

now_ms(){ python3 -I -c 'import time;print(int(time.time()*1000))'; }
die(){ echo "refusing: $*" >&2; exit 2; }

# The database must be named, match curia_<lowercase word characters>, and never be curia_e2e_*, postgres or a template.
check_db(){
  [ -n "$FH_DB" ] || die "FH_DB is not set (name the throwaway database)"
  [[ "$FH_DB" =~ ^curia_[a-z0-9_]+$ ]] || die "database '$FH_DB' does not match curia_[a-z0-9_]+"
  case "$FH_DB" in curia_e2e_*) die "database '$FH_DB' is a curia_e2e_* database";; esac
  local cur; cur=$(psql -d "$FH_DB" -Atc "select current_database()" 2>/dev/null) || die "cannot connect to '$FH_DB'"
  [ "$cur" = "$FH_DB" ] || die "current_database() is '$cur', not '$FH_DB'"
  mkdir -p "$FH_DIR/marks"
}
# fault.sh and proxy.sh also need the role: named, a plain identifier, existing, not a superuser, not the admin session's own user.
check_pair(){
  check_db
  [ -n "$FH_ROLE" ] || die "FH_ROLE is not set (name the Forum's application role)"
  [[ "$FH_ROLE" =~ ^[a-z][a-z0-9_]*$ ]] || die "role '$FH_ROLE' is not a plain lowercase identifier"
  case "$FH_ROLE" in postgres|pg_*) die "role '$FH_ROLE' is not an application role";; esac
  local info; info=$(psql -d "$FH_DB" -Atc "select rolsuper::text || ' ' || (rolname = current_user)::text from pg_roles where rolname = '$FH_ROLE'")
  [ -n "$info" ] || die "role '$FH_ROLE' does not exist"
  [ "$info" = "false false" ] || die "role '$FH_ROLE' is a superuser or the admin session's own user"
}
