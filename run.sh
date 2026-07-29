#!/usr/bin/env bash

# As root (sudo su)
# cd / && curl -s -H "Cache-Control: no-cache" -o "run.sh" "https://raw.githubusercontent.com/kus/cs2-modded-server/master/run.sh" && chmod +x run.sh && bash run.sh

# Function to safely enable unprivileged user namespaces.
# Steam's runtime sandboxes the game with bwrap (pressure-vessel), which needs
# unprivileged user namespaces. Different distro/kernel versions gate these behind
# different sysctls, so we probe for each knob and only touch the ones that exist:
#   - kernel.apparmor_restrict_unprivileged_userns : Ubuntu 24.04+ AppArmor lockdown.
#       Left at its default of 1 it causes "bwrap: setting up uid map: Permission denied".
#   - kernel.unprivileged_userns_clone             : older Ubuntu (<= 23.10) / Debian.
# Settings are also written to /etc/sysctl.d so they survive reboots (GCP VMs reset
# runtime sysctl changes on restart).
enable_unprivileged_namespaces() {
    local persist_file="/etc/sysctl.d/99-cs2-userns.conf"
    local persist=""

    # Ubuntu 24.04+ : AppArmor blocks unprivileged userns even when otherwise allowed.
    if sysctl kernel.apparmor_restrict_unprivileged_userns >/dev/null 2>&1; then
        if [ "$(sysctl -n kernel.apparmor_restrict_unprivileged_userns 2>/dev/null)" != "0" ]; then
            echo "Disabling AppArmor restriction on unprivileged user namespaces (Ubuntu 24.04+)..."
            sudo sysctl -w kernel.apparmor_restrict_unprivileged_userns=0 \
                && echo "Successfully disabled AppArmor userns restriction" \
                || echo "Warning: Failed to disable AppArmor userns restriction"
        else
            echo "AppArmor userns restriction already disabled"
        fi
        persist+="kernel.apparmor_restrict_unprivileged_userns=0"$'\n'
    fi

    # Older Ubuntu / Debian : userns gated behind unprivileged_userns_clone.
    if sysctl kernel.unprivileged_userns_clone >/dev/null 2>&1; then
        if [ "$(sysctl -n kernel.unprivileged_userns_clone 2>/dev/null)" != "1" ]; then
            echo "Enabling unprivileged user namespaces..."
            sudo sysctl -w kernel.unprivileged_userns_clone=1 \
                && echo "Successfully enabled unprivileged user namespaces" \
                || echo "Warning: Failed to enable unprivileged user namespaces"
        else
            echo "Unprivileged user namespaces already enabled"
        fi
        persist+="kernel.unprivileged_userns_clone=1"$'\n'
    fi

    if [ -z "$persist" ]; then
        echo "Info: no unprivileged user namespace sysctls available on this system"
        return 0
    fi

    # Persist so the fix survives reboots.
    { echo "# Managed by cs2-modded-server: allow unprivileged user namespaces for Steam runtime (bwrap)"; \
      printf '%s' "$persist"; } | sudo tee "$persist_file" >/dev/null 2>&1 \
        && echo "Persisted user namespace settings to $persist_file"
    return 0
}

user="steam"
PUBLIC_IP=$(dig +short myip.opendns.com @resolver1.opendns.com)

# 32 or 64 bit Operating System
# If BITS environment variable is not set, try determine it
if [ -z "$BITS" ]; then
    # Determine the operating system architecture
    architecture=$(uname -m)

    # Set OS_BITS based on the architecture
    if [[ $architecture == *"64"* ]]; then
        export BITS=64
    elif [[ $architecture == *"i386"* ]] || [[ $architecture == *"i686"* ]]; then
        export BITS=32
    else
        echo "Unknown architecture: $architecture"
        exit 1
    fi
fi

if [[ -z $IP ]]; then
    IP_ARGS=""
else
    IP_ARGS="-ip ${IP}"
fi

echo "Downloading any updates for Steam Linux Runtime 3.0 (sniper)..."
# https://discord.com/channels/1160907911501991946/1160907912445710479/1411330429679829013
# https://steamdb.info/app/1628350/depots/
sudo -u $user /steamcmd/steamcmd.sh \
  +api_logging 1 1 \
  +@sSteamCmdForcePlatformType linux \
  +@sSteamCmdForcePlatformBitness $BITS \
  +force_install_dir /home/${user}/steamrt \
  +login anonymous \
  +app_update 1628350 \
  +validate \
  +quit
chown -R ${user}:${user} /home/${user}/steamrt

echo "Downloading any updates for CS2..."
# https://developer.valvesoftware.com/wiki/Command_line_options
sudo -u $user /steamcmd/steamcmd.sh \
  +api_logging 1 1 \
  +@sSteamCmdForcePlatformType linux \
  +@sSteamCmdForcePlatformBitness $BITS \
  +force_install_dir /home/${user}/cs2 \
  +login anonymous \
  +app_update 730 \
  +quit

cd /home/${user}/cs2

# Try to enable unprivileged namespaces
enable_unprivileged_namespaces

echo "Starting server on $PUBLIC_IP:$PORT"
echo /home/${user}/steamrt/run ./game/bin/linuxsteamrt64/cs2 --graphics-provider "" -- \
    -dedicated \
    -console \
    -usercon \
    -disable_workshop_command_filtering \
    -autoupdate \
    -tickrate $TICKRATE \
	$IP_ARGS \
    -port $PORT \
    +map de_dust2 \
    +sv_visiblemaxplayers $MAXPLAYERS \
    -authkey $API_KEY \
    +sv_setsteamaccount $STEAM_ACCOUNT \
    +game_type 0 \
    +game_mode 0 \
    +mapgroup mg_active \
    +sv_lan $LAN \
	+sv_password $SERVER_PASSWORD \
	+rcon_password $RCON_PASSWORD \
	+exec $EXEC
sudo -u $user /home/${user}/steamrt/run ./game/bin/linuxsteamrt64/cs2 --graphics-provider "" -- \
    -dedicated \
    -console \
    -usercon \
    -disable_workshop_command_filtering \
    -autoupdate \
    -tickrate $TICKRATE \
	$IP_ARGS \
    -port $PORT \
    +map de_dust2 \
    +sv_visiblemaxplayers $MAXPLAYERS \
    -authkey $API_KEY \
    +sv_setsteamaccount $STEAM_ACCOUNT \
    +game_type 0 \
    +game_mode 0 \
    +mapgroup mg_active \
    +sv_lan $LAN \
	+sv_password $SERVER_PASSWORD \
	+rcon_password $RCON_PASSWORD \
	+exec $EXEC
