#!/usr/bin/env bash
# Run the built image without stdin or published ports, using a disposable data volume.
set -euo pipefail
image=${1:?Usage: docker_smoke.sh IMAGE}
volume=$(docker volume create)
container=
cleanup() {
    if [[ -n "$container" ]]; then
        docker logs "$container" >&2 || true
        docker rm -f "$container" >/dev/null || true
    fi
    docker volume rm "$volume" >/dev/null || true
}
trap cleanup EXIT

boot_and_stop() {
    local ready=false
    container=$(docker run -d --network none --mount "type=volume,src=$volume,dst=/data" "$image")
    for ((attempt=0; attempt<30; attempt++)); do
        if [[ "$(docker inspect -f '{{.State.Running}}' "$container")" != true ]]; then
            echo "Server exited before becoming ready." >&2
            return 1
        fi
        if [[ "$(docker logs "$container" 2>&1)" == *"listening on"* ]]; then
            ready=true
            break
        fi
        sleep 1
    done
    if [[ "$ready" != true ]]; then
        echo "Server did not become ready in 30 seconds." >&2
        return 1
    fi
    docker stop -t 10 "$container" >/dev/null
    test "$(docker inspect -f '{{.State.ExitCode}}' "$container")" = 0
    docker rm "$container" >/dev/null
    container=
}

boot_and_stop
docker run --rm --network none --mount "type=volume,src=$volume,dst=/data,readonly" \
    --entrypoint sh "$image" -ec '
    test -s /data/server.conf
    test -f /data/credentials
    test -f /data/users
    test -f /data/framework-extensions/server/builtin.chat/chat-history.bin
    test -s /data/framework-extensions/server/builtin.trade/trade-state.bin.sqlite3
'
boot_and_stop
echo "Server image passed two no-stdin boots and SIGTERM stops with the same data volume."
