#!/bin/bash
# PPDS devcontainer post-create setup
# Configures Claude Code auth + marketplace, restores .NET packages
set -e

CLAUDE_DIR="$HOME/.claude"
PLUGINS_DIR="$CLAUDE_DIR/plugins"
STAGED_CONFIG="/tmp/host-claude-config.json"
STAGED_SETTINGS="/tmp/host-claude-settings.json"

# --- Step 0: Mark workspace as safe for git ---
echo "=== Configuring git safe directories ==="
WORKSPACE_ROOT="$(pwd)"
git config --global --add safe.directory "$WORKSPACE_ROOT"
if [ -d ".worktrees" ]; then
    for wt_dir in .worktrees/*/; do
        wt_path="$WORKSPACE_ROOT/$(echo "$wt_dir" | sed 's:/$::')"
        git config --global --add safe.directory "$wt_path"
        echo "Safe directory: $wt_path"
    done
fi
echo "Safe directory: $WORKSPACE_ROOT"

# --- Step 0a: Configure git identity from host ---
echo "=== Configuring git identity ==="
HOST_GITCONFIG="/tmp/host-gitconfig"
if [ -f "$HOST_GITCONFIG" ]; then
    GIT_NAME=$(git config -f "$HOST_GITCONFIG" user.name 2>/dev/null)
    GIT_EMAIL=$(git config -f "$HOST_GITCONFIG" user.email 2>/dev/null)
    if [ -n "$GIT_NAME" ] && [ -n "$GIT_EMAIL" ]; then
        git config --global user.name "$GIT_NAME"
        git config --global user.email "$GIT_EMAIL"
        echo "Git identity: $GIT_NAME <$GIT_EMAIL>"
    else
        echo "WARNING: Host .gitconfig missing user.name or user.email."
    fi
else
    echo "WARNING: Host .gitconfig not found. Git commits will require manual config."
fi

# --- Step 1: Extract oauthAccount from staged host config ---
echo "=== Configuring Claude Code authentication ==="
if [ -f "$STAGED_CONFIG" ]; then
    node -e "
        const fs = require('fs');
        const host = JSON.parse(fs.readFileSync('$STAGED_CONFIG', 'utf8'));
        if (host.oauthAccount) {
            const clean = {
                oauthAccount: host.oauthAccount,
                hasCompletedOnboarding: true
            };
            fs.writeFileSync('$HOME/.claude.json', JSON.stringify(clean, null, 2));
            console.log('Auth config created for: ' + host.oauthAccount.emailAddress);
        } else {
            console.warn('WARNING: No oauthAccount in host config. Manual login required.');
        }
    "
else
    echo "WARNING: Host config not found at $STAGED_CONFIG. Manual login required."
fi

# --- Step 2: Copy settings.json if available ---
echo "=== Configuring Claude Code settings ==="
if [ -f "$STAGED_SETTINGS" ]; then
    mkdir -p "$CLAUDE_DIR"
    cp "$STAGED_SETTINGS" "$CLAUDE_DIR/settings.json"
    echo "Settings copied from host."
else
    echo "No host settings found. Using defaults."
fi

# --- Step 3: Initialize marketplace with Linux paths ---
echo "=== Setting up Claude Code marketplace ==="
mkdir -p "$PLUGINS_DIR/marketplaces"

if [ ! -d "$PLUGINS_DIR/marketplaces/claude-plugins-official" ]; then
    echo "Cloning Anthropic official marketplace..."
    git clone --depth 1 https://github.com/anthropics/claude-plugins-official.git \
        "$PLUGINS_DIR/marketplaces/claude-plugins-official"
    echo "Marketplace cloned."
else
    echo "Marketplace already present, skipping clone."
fi

node -e "
    const fs = require('fs');
    const marketplaces = {
        'claude-plugins-official': {
            source: { source: 'github', repo: 'anthropics/claude-plugins-official' },
            installLocation: '$PLUGINS_DIR/marketplaces/claude-plugins-official',
            lastUpdated: new Date().toISOString()
        }
    };
    fs.mkdirSync('$PLUGINS_DIR', { recursive: true });
    fs.writeFileSync('$PLUGINS_DIR/known_marketplaces.json', JSON.stringify(marketplaces, null, 2));
    console.log('Marketplace registry written with Linux paths.');
"

# --- Step 4: Restore .NET packages ---
echo "=== Restoring .NET packages ==="
dotnet restore PPDS.sln

echo "=== Setup complete ==="
