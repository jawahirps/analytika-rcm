#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# analytika-ctl.sh — Analytika server controller (macOS / Linux)
#
# Usage:
#   ./scripts/analytika-ctl.sh [run|bgrun|pause|resume|restart|stop|status]
#
# Commands
#   run      Start the full web server (port 5000, background jobs OFF)
#   bgrun    Background-only mode: fetch & save jobs, no public HTTP port
#   pause    Freeze the process in memory  (SIGSTOP — zero CPU, keeps port)
#   resume   Unfreeze a paused process     (SIGCONT)
#   restart  Stop then start (same mode as last run)
#   stop     Gracefully stop (SIGTERM → wait 10 s → SIGKILL if needed)
#   status   Show running state, PID, uptime, and port
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# ── Paths ─────────────────────────────────────────────────────────────────────
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
APP_DIR="$REPO_ROOT/Analytika"
RUN_DIR="$REPO_ROOT/.run"
WEB_PID="$RUN_DIR/web.pid"
BG_PID="$RUN_DIR/worker.pid"
MOD_FILE="$RUN_DIR/mode"
WEB_LOG="$RUN_DIR/web.log"
BG_LOG="$RUN_DIR/worker.log"
ENV_FILE="$REPO_ROOT/.env"

ACTION="${1:-status}"

# ── Colours ───────────────────────────────────────────────────────────────────
GREEN='\033[0;32m'; CYAN='\033[0;36m'; YELLOW='\033[1;33m'
RED='\033[0;31m'; RESET='\033[0m'; BOLD='\033[1m'
ok()   { echo -e "  ${GREEN}[OK]${RESET}  $*"; }
err()  { echo -e "  ${RED}[FAIL]${RESET} $*"; }
info() { echo -e "  ${CYAN}[--]${RESET}  $*"; }
warn() { echo -e "  ${YELLOW}[!!]${RESET}  $*"; }

# ── .env loader ───────────────────────────────────────────────────────────────
load_env() {
    [[ -f "$ENV_FILE" ]] || return 0
    while IFS='=' read -r key val; do
        [[ "$key" =~ ^[[:space:]]*# ]] && continue
        [[ -z "$key" ]] && continue
        key="${key// /}"
        val="${val%$'\r'}"   # strip Windows CRLF
        [[ -z "${!key+x}" ]] && export "$key=$val"
    done < "$ENV_FILE"
}

# ── PID helpers ───────────────────────────────────────────────────────────────
get_pid() {
    local file="$1"
    [[ -f "$file" ]] || return 1
    local id; id=$(cat "$file" | tr -d '[:space:]')
    kill -0 "$id" 2>/dev/null && echo "$id" || { rm -f "$file"; return 1; }
}

save_pid() { mkdir -p "$RUN_DIR"; echo "$1" > "$2"; }

pid_uptime() {
    local id="$1"
    if [[ "$(uname)" == "Darwin" ]]; then
        local start; start=$(ps -o lstart= -p "$id" 2>/dev/null | xargs -I{} date -j -f "%a %b %d %T %Y" "{}" +%s 2>/dev/null || echo 0)
        local now; now=$(date +%s)
        local secs=$(( now - start ))
    else
        local secs; secs=$(awk '{print int($14/100)}' /proc/"$id"/stat 2>/dev/null || echo 0)
        local hz; hz=$(getconf CLK_TCK 2>/dev/null || echo 100)
        local boot; boot=$(awk '{print int($1)}' /proc/uptime 2>/dev/null || echo 0)
        # simpler: use /proc/$id/stat starttime
        local start_ticks; start_ticks=$(awk '{print $22}' /proc/"$id"/stat 2>/dev/null || echo 0)
        local uptime_sec; uptime_sec=$(awk '{print int($1)}' /proc/uptime 2>/dev/null || echo 0)
        secs=$(( uptime_sec - start_ticks / hz ))
    fi
    printf '%02dh %02dm %02ds' $(( secs/3600 )) $(( (secs%3600)/60 )) $(( secs%60 ))
}

# ── Process launcher ──────────────────────────────────────────────────────────
start_process() {
    local mode="$1"
    load_env

    local DB_DIR="${DB_DIR:-J:/GhafAnalytika/Analytika}"

    # On macOS the path is typically a real FS path
    if [[ "$(uname)" == "Darwin" && "$DB_DIR" == J:/* ]]; then
        DB_DIR="/Volumes/GhafAnalytika/Analytika"
        warn "Adjusted DB_DIR to macOS path: $DB_DIR"
    fi

    local COMMON=(
        "DB_DIR=$DB_DIR"
        "ASPNETCORE_ENVIRONMENT=Production"
        "StartupMaintenance__RunDatabaseSetupOnStartup=false"
        "StartupMaintenance__CreateIndexesOnStartup=false"
        "StartupMaintenance__SeedDataOnStartup=false"
    )

    if [[ "$mode" == "web" ]]; then
        local VARS=(
            "${COMMON[@]}"
            "ASPNETCORE_URLS=http://+:5000"
            "BackgroundJobs__HangfireServerEnabled=false"
            "BackgroundJobs__RecurringJobsEnabled=false"
            "BackgroundJobs__HangfireDashboardEnabled=false"
            "BackgroundJobs__PendingDownloads__HostedServiceEnabled=false"
        )
        local LOG="$WEB_LOG"
        local PF="$WEB_PID"
    else
        local VARS=(
            "${COMMON[@]}"
            "ASPNETCORE_URLS=http://127.0.0.1:8081"
            "StartupMaintenance__RunDatabaseSetupOnStartup=true"
            "StartupMaintenance__CreateIndexesOnStartup=true"
            "BackgroundJobs__HangfireServerEnabled=true"
            "BackgroundJobs__RecurringJobsEnabled=true"
            "BackgroundJobs__HangfireDashboardEnabled=false"
            "BackgroundJobs__PendingDownloads__HostedServiceEnabled=true"
            "BackgroundJobs__PendingDownloads__ScheduledRunEnabled=true"
            "BackgroundJobs__PendingDownloads__ScheduledLocalTime=02:30"
            "BackgroundJobs__PendingDownloads__AutoParseRemittance=true"
            "BackgroundJobs__PendingDownloads__BatchSize=25"
        )
        local LOG="$BG_LOG"
        local PF="$BG_PID"
    fi

    [[ -n "${ANTHROPIC_API_KEY:-}" ]] && VARS+=("Anthropic__ApiKey=$ANTHROPIC_API_KEY")

    mkdir -p "$RUN_DIR"
    (
        for kv in "${VARS[@]}"; do
            export "$kv"
        done
        cd "$APP_DIR"
        exec dotnet run --no-build >> "$LOG" 2>&1
    ) &
    local pid=$!
    save_pid "$pid" "$PF"
    echo "$mode" > "$MOD_FILE"
    echo "$pid"
}

# ── ACTION: status ────────────────────────────────────────────────────────────
cmd_status() {
    echo ""
    echo -e "  ${BOLD}Analytika Server Status${RESET}"
    echo -e "  ────────────────────────────────────────"

    local webId bgId
    webId=$(get_pid "$WEB_PID" 2>/dev/null || true)
    bgId=$(get_pid "$BG_PID" 2>/dev/null || true)

    if [[ -n "$webId" ]]; then
        local up; up=$(pid_uptime "$webId")
        ok "WEB SERVER  PID $webId  |  up $up"
        info "            Listening on http://+:5000"
    else
        warn "WEB SERVER  not running"
    fi

    if [[ -n "$bgId" ]]; then
        local up; up=$(pid_uptime "$bgId")
        ok "BG WORKER   PID $bgId   |  up $up"
        info "            Loopback only — fetch & save jobs active"
    else
        info "BG WORKER   not running"
    fi
    echo ""
}

# ── ACTION: run ───────────────────────────────────────────────────────────────
cmd_run() {
    if get_pid "$WEB_PID" &>/dev/null; then
        warn "Web server already running. Use 'restart' to reload."
        return
    fi
    info "Starting web server on http://+:5000 ..."
    local pid; pid=$(start_process web)
    sleep 8
    if curl -sf "http://localhost:5000/healthz" &>/dev/null; then
        ok "Web server running  PID $pid  →  http://localhost:5000"
    else
        warn "Server started (PID $pid) but /healthz not responding yet — give it a few more seconds."
        info "Tail logs:  tail -f $WEB_LOG"
    fi
}

# ── ACTION: bgrun ─────────────────────────────────────────────────────────────
cmd_bgrun() {
    if get_pid "$BG_PID" &>/dev/null; then
        warn "Background worker already running. Use 'stop' first."
        return
    fi
    info "Starting background worker (fetch & save only, loopback 8081) ..."
    local pid; pid=$(start_process worker)
    sleep 5
    if get_pid "$BG_PID" &>/dev/null; then
        ok "Background worker running  PID $pid"
        info "Jobs: dha-daily-sync  remittance-auto-parse  db-nightly-backup  data-retention"
        info "Tail logs:  tail -f $BG_LOG"
    else
        err "Worker failed to stay up. Check:  tail -30 $BG_LOG"
    fi
}

# ── ACTION: pause ─────────────────────────────────────────────────────────────
cmd_pause() {
    local any=false
    for pf in "$WEB_PID" "$BG_PID"; do
        local id; id=$(get_pid "$pf" 2>/dev/null || true)
        [[ -z "$id" ]] && continue
        kill -STOP "$id"
        ok "Paused PID $id  (SIGSTOP — frozen in memory, zero CPU)"
        any=true
    done
    $any || warn "No running process to pause."
}

# ── ACTION: resume ────────────────────────────────────────────────────────────
cmd_resume() {
    local any=false
    for pf in "$WEB_PID" "$BG_PID"; do
        local id; id=$(get_pid "$pf" 2>/dev/null || true)
        [[ -z "$id" ]] && continue
        kill -CONT "$id"
        ok "Resumed PID $id  (SIGCONT)"
        any=true
    done
    $any || warn "No paused process found."
}

# ── ACTION: stop ──────────────────────────────────────────────────────────────
cmd_stop() {
    local any=false
    for pf in "$WEB_PID" "$BG_PID"; do
        local id; id=$(get_pid "$pf" 2>/dev/null || true)
        [[ -z "$id" ]] && continue
        kill -TERM "$id" 2>/dev/null || true
        local waited=0
        while kill -0 "$id" 2>/dev/null && (( waited < 10 )); do
            sleep 1; (( waited++ )) || true
        done
        kill -0 "$id" 2>/dev/null && kill -KILL "$id" && warn "Force-killed PID $id" || ok "Stopped PID $id"
        rm -f "$pf"
        any=true
    done
    $any || info "No running process to stop."
}

# ── ACTION: restart ───────────────────────────────────────────────────────────
cmd_restart() {
    local mode="web"
    [[ -f "$MOD_FILE" ]] && mode=$(cat "$MOD_FILE")
    info "Restarting in '$mode' mode ..."
    cmd_stop
    sleep 2
    if [[ "$mode" == "worker" ]]; then cmd_bgrun; else cmd_run; fi
}

# ── Dispatch ──────────────────────────────────────────────────────────────────
case "$ACTION" in
    run)     cmd_run     ;;
    bgrun)   cmd_bgrun   ;;
    pause)   cmd_pause   ;;
    resume)  cmd_resume  ;;
    restart) cmd_restart ;;
    stop)    cmd_stop    ;;
    status)  cmd_status  ;;
    *)
        echo "Usage: $0 {run|bgrun|pause|resume|restart|stop|status}"
        exit 1
        ;;
esac
