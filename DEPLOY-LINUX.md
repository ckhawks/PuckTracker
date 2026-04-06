# Deploying PuckServerTracker on Ubuntu VPS

## Prerequisites

- Ubuntu VPS (20.04+)
- Steam account that owns Puck (free-to-play, AppID 2994020)
- PostgreSQL database with the `psl_` tables created (run `migrations/003_puck_server_list.sql`)

## 1. Publish (on your Windows dev machine)

```bash
cd "C:/Projects/Puck Plugins/PuckServerTracker/PuckTracker"
dotnet publish -r linux-x64 --self-contained -o publish/linux-x64
```

This creates a self-contained binary — no .NET install needed on the VPS.

## 2. Upload to VPS

```bash
scp -r PuckTracker/publish/linux-x64/ user@your-vps:~/pucktracker/
scp PuckTracker/GeoLite2-City.mmdb PuckTracker/GeoLite2-ASN.mmdb user@your-vps:~/pucktracker/
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

## Updating

To deploy a new version:

```bash
# On Windows: rebuild
dotnet publish -r linux-x64 --self-contained -o publish/linux-x64

# Upload new binary
scp PuckTracker/publish/linux-x64/PuckTracker user@your-vps:~/pucktracker/PuckTracker

# Restart on VPS
ssh user@your-vps "sudo systemctl restart pucktracker"
```

The `.env`, `steam_credentials.json`, and `.mmdb` files persist between updates.

## Troubleshooting

- **SteamAPI login failed**: Delete `steam_credentials.json` and run interactively
  again to re-authenticate
- **Database connection failed**: Check `DATABASE_URL` in `.env`
- **b202 connection fails**: The b202 master server (`puck1.nasejevs.com`) has an
  expired SSL cert. The app bypasses this automatically, but some firewalls may
  block it
- **No servers found**: Make sure the `psl_` tables exist in PostgreSQL
  (run the migration)
