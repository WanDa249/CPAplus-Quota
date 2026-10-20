#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
temp_dir=$(mktemp -d)
server_pid=""
trap 'if [[ -n "$server_pid" ]]; then kill "$server_pid" 2>/dev/null || true; fi; rm -rf "$temp_dir"' EXIT
cat > "$temp_dir/cert.cnf" <<'EOF'
[req]
distinguished_name = dn
x509_extensions = extensions
prompt = no
[dn]
CN = localhost
[extensions]
basicConstraints = critical,CA:TRUE
keyUsage = critical,digitalSignature,keyEncipherment,keyCertSign
extendedKeyUsage = serverAuth
subjectAltName = DNS:localhost,IP:127.0.0.1
EOF
openssl req -x509 -newkey rsa:2048 -nodes -days 1 -config "$temp_dir/cert.cnf" \
  -keyout "$temp_dir/key.pem" -out "$temp_dir/cert.pem" 2>/dev/null
openssl x509 -in "$temp_dir/cert.pem" -outform DER -out "$temp_dir/cert.der"
python3 scripts/mock_https.py --cert "$temp_dir/cert.pem" --key "$temp_dir/key.pem" --port-file "$temp_dir/port" &
server_pid=$!
for _ in {1..50}; do
  [[ -s "$temp_dir/port" ]] && break
  sleep 0.1
done
test -s "$temp_dir/port"
export QUOTA_TEST_ORIGIN="https://localhost:$(cat "$temp_dir/port")"
export QUOTA_TEST_CERT="$temp_dir/cert.der"
swift test --no-parallel
