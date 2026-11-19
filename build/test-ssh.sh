#!/usr/bin/env bash
# SSH bastion for the SSH-tunnel live tests (LiveSshTunnelTests). Puts an openssh-server container and the
# MariaDB test container (./build/test-db-mysql.sh) on one docker network, so the bastion reaches the database
# by container name — the way a real bastion reaches a private database host.
#   user bearing / password sshpass, on 127.0.0.1:2223
set -euo pipefail
NET=bearing-test
NAME=bearing-ssh-test
# Pinned: the AllowTcpForwarding rewrite below depends on this image's config layout, and a newer image could
# move it and leave the tests unable to forward. Bump deliberately, after running LiveSshTunnelTests on it.
IMAGE=lscr.io/linuxserver/openssh-server:10.3_p1-r2-ls242
docker network inspect "$NET" >/dev/null 2>&1 || docker network create "$NET" >/dev/null
docker network connect "$NET" bearing-mariadb-test 2>/dev/null || true
if ! docker inspect "$NAME" >/dev/null 2>&1; then
  docker run -d --name "$NAME" --network "$NET" \
    -e PUID=1000 -e PGID=1000 -e PASSWORD_ACCESS=true -e USER_NAME=bearing -e USER_PASSWORD=sshpass \
    -p 127.0.0.1:2223:2222 "$IMAGE" >/dev/null
fi
docker network connect "$NET" "$NAME" 2>/dev/null || true
for _ in $(seq 1 30); do docker exec "$NAME" test -f /config/sshd/sshd_config 2>/dev/null && break; sleep 1; done
# The image ships with forwarding off; a bastion that cannot forward is not one.
if docker exec "$NAME" grep -q '^AllowTcpForwarding no' /config/sshd/sshd_config; then
  docker exec "$NAME" sed -i 's/^AllowTcpForwarding no/AllowTcpForwarding yes/' /config/sshd/sshd_config
  docker restart "$NAME" >/dev/null
fi
echo "ssh bastion: bearing@127.0.0.1:2223 (password sshpass) -> bearing-mariadb-test:3306"
