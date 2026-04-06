# Deploying PuckServerTracker on a Linux VPS

## Prerequisites
- Dummy Steam account that **owns Puck** (AppID 2994020)
- Linux VPS with at least 1GB RAM (Ubuntu/Debian recommended)

## 1. Install Steam Client

```bash
# Enable 32-bit arch (Steam needs it)
sudo dpkg --add-architecture i386
sudo apt update

# Install Steam
sudo apt install -y steam steamcmd

# Or manually:
# wget https://cdn.cloudflare.steamstatic.com/client/installer/steam.deb
# sudo dpkg -i steam.deb
# sudo apt-get install -f -y
```

## 2. Set up headless Steam (no GUI needed)

```bash
# Create a steam user (don't run Steam as root)
sudo useradd -m -s /bin/bash steamuser
sudo su - steamuser

# Log in via SteamCMD first to cache credentials
steamcmd +login YOUR_DUMMY_USERNAME YOUR_PASSWORD +quit

# Install Puck dedicated server (free, gets us the steam libraries)
steamcmd +login YOUR_DUMMY_USERNAME +app_update 3004430 +quit
```

Note: The dummy account needs to own Puck. If Puck is free-to-play, just
"install" it to the account via Steam. If paid, buy it on the dummy account.

## 3. Install .NET 9 Runtime

```bash
# Ubuntu/Debian
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --runtime dotnet --version 9.0.0
echo 'export PATH=$PATH:$HOME/.dotnet' >> ~/.bashrc
source ~/.bashrc
```

Or use the self-contained publish (no .NET install needed) - the published
binary already includes the runtime.

## 4. Deploy PuckTracker

```bash
# Upload the publish/linux-x64/ folder to your VPS
# e.g. via scp:
scp -r publish/linux-x64/ user@your-vps:/home/steamuser/PuckTracker/

# On the VPS:
sudo su - steamuser
cd ~/PuckTracker
chmod +x PuckTracker

# The Steam client's libsteam_api.so needs to be findable.
# Link or copy it from the Steam installation:
ln -s ~/.steam/steam/linux64/libsteam_api.so ./libsteam_api.so
# or from the dedicated server:
# ln -s ~/Steam/steamapps/common/Puck\ Dedicated\ Server/Puck_Data/Plugins/x86_64/libsteam_api.so ./libsteam_api.so

# Make sure steam_appid.txt is present (should be in the folder already)
cat steam_appid.txt  # should show: 2994020
```

## 5. Run Steam Client in Background

The tracker needs the Steam client running (not just SteamCMD) because
it calls `SteamUser.GetAuthTicketForWebApi()` which requires a full client
session.

```bash
# Install virtual framebuffer for headless operation
sudo apt install -y xvfb

# Start Steam headless
export DISPLAY=:99
Xvfb :99 -screen 0 1024x768x24 &
steam -no-browser -no-dwrite -silent &

# Wait for Steam to start up (check with):
sleep 10
```

Alternative: if the full Steam client is too heavy, you may be able to use
the Steam runtime libraries directly. The key file needed is `libsteam_api.so`
and Steam must be running for auth tickets to work.

## 6. Run PuckTracker

```bash
cd ~/PuckTracker

# Test single scan first
./PuckTracker --once

# Run continuously (every 5 minutes by default)
./PuckTracker

# Run as a background service with systemd (recommended):
```

## 7. Systemd Service (optional, recommended)

Create `/etc/systemd/system/pucktracker.service`:

```ini
[Unit]
Description=Puck Server Tracker
After=network.target

[Service]
Type=simple
User=steamuser
WorkingDirectory=/home/steamuser/PuckTracker
ExecStart=/home/steamuser/PuckTracker/PuckTracker
Restart=always
RestartSec=30
Environment=LD_LIBRARY_PATH=/home/steamuser/PuckTracker

[Install]
WantedBy=multi-user.target
```

Then:
```bash
sudo systemctl daemon-reload
sudo systemctl enable pucktracker
sudo systemctl start pucktracker
sudo journalctl -u pucktracker -f  # watch logs
```

## Troubleshooting

- **SteamAPI.Init() failed**: Steam client isn't running, or steam_appid.txt
  is missing, or the account doesn't own Puck
- **libsteam_api.so not found**: Set `LD_LIBRARY_PATH` to include the dir
  with the .so file, or copy/symlink it next to the PuckTracker binary
- **Auth ticket failed**: Steam session expired, restart Steam client
- **Connection refused to puck1**: The b202 master server has an expired SSL
  cert - the app bypasses this, but your VPS firewall might block outbound
  port 443
