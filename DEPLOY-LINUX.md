# Deploying PuckServerTracker on Ubuntu VPS

## Prerequisites

- Ubuntu VPS (20.04+)
- Steam account that owns Puck (free-to-play, AppID 2994020)
- PostgreSQL database with the `psl_` tables created (run `migrations/003_puck_server_list.sql`)

## 1. Publish (on your Windows dev machine)

```bash
cd "C:/Projects/Puck Plugins/PuckServerTracker"
dotnet publish -r linux-x64 --self-contained -o publish/linux-x64
```

This creates a self-contained binary — no .NET install needed on the VPS.

## 2. Upload to VPS

```bash
scp -r publish/linux-x64/ user@your-vps:~/pucktracker/
scp GeoLite2-City.mmdb GeoLite2-ASN.mmdb user@your-vps:~/pucktracker/
```

## 3. Configure

```bash
ssh user@your-vps
cd ~/pucktracker
chmod +x PuckTracker

# Create .env
cat > .env << 'EOF'
DATABASE_URL=postgresql://user:pass@host:port/dbname
EOF
```

## 4. First run (interactive)

The first run requires interactive input for Steam login:

```bash
./PuckTracker --once
```

You'll be prompted for:

1. Steam username
2. Steam password (hidden input)
3. Steam Guard code (email or authenticator)

After successful login, a `steam_credentials.json` file is saved with a refresh
token. All future runs authenticate automatically without prompting.

## 5. Set up systemd service

```bash
sudo tee /etc/systemd/system/pucktracker.service > /dev/null << 'EOF'
[Unit]
Description=Puck Server Tracker
After=network.target

[Service]
Type=simple
User=YOUR_USER
WorkingDirectory=/root/PuckTracker
ExecStart=/root/PuckTracker/PuckTracker
Restart=always
RestartSec=30

[Install]
WantedBy=multi-user.target
EOF

sudo systemctl daemon-reload
sudo systemctl enable pucktracker
sudo systemctl start pucktracker
```

## 6. Monitor

```bash
# Watch live logs
sudo journalctl -u pucktracker -f

# Check status
sudo systemctl status pucktracker

# Restart after updating
sudo systemctl restart pucktracker
```

## Where it runs

Dallas (`extravm-puckvps-1`, 216.146.25.22), unit `pucktracker.service` (lowercase),
from `/root/PuckTracker`. Reach it with `ssh dallas`.

## Updating

The publish is **not** single-file: `PuckTracker` is only the apphost launcher and the
code lives in `PuckTracker.dll`. Copying just `PuckTracker` deploys nothing.

Which files to copy depends on whether your .NET SDK has been patched since the last
deploy. Diff before deciding, rather than guessing:

```bash
# On Windows: rebuild
dotnet publish -r linux-x64 --self-contained -o publish/linux-x64

# Compare against the box
cd publish/linux-x64
md5sum * | sed 's/ \*/  /' | awk '{print $2, $1}' | sort > /tmp/local.md5
ssh dallas "cd /root/PuckTracker && md5sum *" | awk '{print $2, $1}' | sort > /tmp/box.md5
join /tmp/local.md5 /tmp/box.md5 | awk '$2!=$3{print $1}'
```

If only `PuckTracker.dll` and `PuckTracker.pdb` differ, copy those two:

```bash
scp PuckTracker.dll PuckTracker.pdb dallas:/root/PuckTracker/
ssh dallas "chown root:root /root/PuckTracker/PuckTracker.*  && systemctl restart pucktracker"
```

If the runtime assemblies differ too, replace the whole directory — mixing runtime
versions is not safe. Skip the GeoIP databases, which are 76 MB and rarely change:

```bash
cd publish/linux-x64
tar czf /tmp/deploy.tgz --exclude="GeoLite2-*.mmdb" .
scp /tmp/deploy.tgz dallas:/root/
ssh dallas 'systemctl stop pucktracker
  cd /root/PuckTracker && tar czf /root/pucktracker-backup-$(date +%Y%m%d).tgz --exclude="GeoLite2-*.mmdb" .
  tar xzf /root/deploy.tgz -C /root/PuckTracker
  chown -R root:root /root/PuckTracker && chmod +x /root/PuckTracker/PuckTracker
  systemctl start pucktracker'
```

`tar` from Windows carries Windows ownership, hence the `chown` — without it the files
land owned by a numeric UID that does not exist on the box.

The `.env`, `steam_credentials.json`, and `.mmdb` files persist between updates: none of
them are in the publish output, so an extract over the directory leaves them alone.

## Verifying a deploy

`systemctl is-active` only proves the process started. Confirm it is actually collecting:

```sql
SELECT sp.id, sp.timestamp, count(*) AS servers,
       count(sn.client_required_mod_ids) AS with_mods
FROM psl_snapshots sp
JOIN psl_server_snapshots sn ON sn.snapshot_id = sp.id
GROUP BY sp.id, sp.timestamp ORDER BY sp.id DESC LIMIT 5;
```

A snapshot with servers but `with_mods = 0` means the preview parse is broken while the
scan still looks healthy — that exact failure went unnoticed for three months.

## Troubleshooting

- **SteamAPI login failed**: Delete `steam_credentials.json` and run interactively
  again to re-authenticate
- **Database connection failed**: Check `DATABASE_URL` in `.env`
- **No servers found**: Make sure the `psl_` tables exist in PostgreSQL
  (run the migration)
