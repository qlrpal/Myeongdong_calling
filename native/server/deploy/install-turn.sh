#!/usr/bin/env bash
# Run with sudo: install-turn.sh APP_DIR PUBLIC_IP PRIVATE_IP REALM NODE_BINARY [OLD_PID]
set -euo pipefail
app_dir=$(realpath "$1")
public_ip=$2
private_ip=$3
realm=$4
node_binary=$5
old_pid=${6:-}
source_dir=$(cd "$(dirname "$0")/.." && pwd)
[[ $EUID == 0 && -d "$app_dir/native/server" && -x "$node_binary" ]]
[[ $public_ip =~ ^[0-9.]+$ && $private_ip =~ ^[0-9.]+$ && $realm =~ ^[a-zA-Z0-9.-]+$ ]]
"$node_binary" --check "$source_dir/server.js"
"$node_binary" --check "$source_dir/ice-config.js"
backup_dir="/var/backups/voice-native/$(date -u +%Y%m%dT%H%M%SZ)"
install -d -m 700 "$backup_dir"
cp -a "$app_dir/native/server/server.js" "$backup_dir/server.js"
[[ ! -f /etc/turnserver.conf ]] || cp -a /etc/turnserver.conf "$backup_dir/turnserver.conf"
[[ ! -f "$app_dir/native/server/ice-config.js" ]] || cp -a "$app_dir/native/server/ice-config.js" "$backup_dir/ice-config.js"
install -d -m 750 -o root -g ubuntu /etc/voice-native
if [[ ! -s /etc/voice-native/turn.secret ]]; then
  umask 077
  openssl rand -hex 32 > /etc/voice-native/turn.secret
fi
chown root:ubuntu /etc/voice-native/turn.secret
chmod 640 /etc/voice-native/turn.secret
secret=$(cat /etc/voice-native/turn.secret)
umask 077
cat > /etc/turnserver.conf <<EOF
listening-port=3478
listening-ip=$private_ip
relay-ip=$private_ip
external-ip=$public_ip/$private_ip
min-port=49160
max-port=49259
realm=$realm
fingerprint
use-auth-secret
static-auth-secret=$secret
stale-nonce=600
user-quota=12
total-quota=100
max-bps=1000000
no-tls
no-dtls
no-tcp-relay
no-cli
no-multicast-peers
denied-peer-ip=0.0.0.0-0.255.255.255
denied-peer-ip=10.0.0.0-10.255.255.255
denied-peer-ip=127.0.0.0-127.255.255.255
denied-peer-ip=169.254.0.0-169.254.255.255
denied-peer-ip=172.16.0.0-172.31.255.255
denied-peer-ip=192.168.0.0-192.168.255.255
syslog
EOF
unset secret
chown root:turnserver /etc/turnserver.conf
chmod 640 /etc/turnserver.conf
cat > /etc/voice-native/server.env <<EOF
HOST=127.0.0.1
PORT=3000
TURN_URLS=turn:$realm:3478?transport=udp
TURN_SECRET_FILE=/etc/voice-native/turn.secret
TURN_TTL_SECONDS=3600
EOF
cat > /etc/systemd/system/voice-native.service <<EOF
[Unit]
Description=Native voice signaling and temporary TURN credentials
After=network-online.target coturn.service
Wants=network-online.target

[Service]
Type=simple
User=ubuntu
Group=ubuntu
WorkingDirectory=$app_dir
EnvironmentFile=/etc/voice-native/server.env
ExecStart=$node_binary native/server/server.js
Restart=on-failure
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true
UMask=0077

[Install]
WantedBy=multi-user.target
EOF
install -m 644 -o ubuntu -g ubuntu "$source_dir/server.js" "$app_dir/native/server/server.js"
install -m 644 -o ubuntu -g ubuntu "$source_dir/ice-config.js" "$app_dir/native/server/ice-config.js"
# Insert ahead of OCI Ubuntu's final reject; preserve existing SSH/HTTPS rules.
iptables -C INPUT -p udp --dport 3478 -j ACCEPT 2>/dev/null || iptables -I INPUT 1 -p udp --dport 3478 -j ACCEPT
iptables -C INPUT -p udp --dport 49160:49259 -j ACCEPT 2>/dev/null || iptables -I INPUT 1 -p udp --dport 49160:49259 -j ACCEPT
iptables-save > /etc/iptables/rules.v4
systemctl enable coturn
systemctl restart coturn
systemctl is-active --quiet coturn
if [[ -n "$old_pid" && -d /proc/$old_pid ]]; then
  [[ $(readlink -f "/proc/$old_pid/cwd") == "$app_dir" ]]
  [[ $(readlink -f "/proc/$old_pid/exe") == "$node_binary" ]]
  kill -TERM "$old_pid"
  for attempt in {1..30}; do [[ ! -d /proc/$old_pid ]] && break; sleep 0.1; done
fi
systemctl daemon-reload
systemctl enable --now voice-native
systemctl restart voice-native
for attempt in {1..30}; do
  if curl -fsS http://127.0.0.1:3000/health >/dev/null; then
    echo "TURN and voice-native active. Backup: $backup_dir"
    exit 0
  fi
  sleep 0.2
done
echo 'Signaling health check failed; inspect journalctl -u voice-native.' >&2
exit 1
