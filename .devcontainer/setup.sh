#!/bin/bash
# PPDS devcontainer post-create setup
set -e

PLUGINS_DIR="$HOME/.claude/plugins"

# Fix volume ownership (Docker creates named volumes as root)
sudo chown -R vscode:vscode "$PLUGINS_DIR" 2>/dev/null || true

echo "=== Restoring .NET packages ==="
dotnet restore PPDS.sln

echo "=== Setting up Claude Code marketplace ==="
if [ ! -d "$PLUGINS_DIR/marketplaces/claude-plugins-official" ]; then
    echo "Cloning Anthropic official marketplace..."
    mkdir -p "$PLUGINS_DIR/marketplaces"
    git clone https://github.com/anthropics/claude-plugins-official.git \
        "$PLUGINS_DIR/marketplaces/claude-plugins-official"

    # Write marketplace registry with Linux paths
    cat > "$PLUGINS_DIR/known_marketplaces.json" << 'MKJSON'
{
  "claude-plugins-official": {
    "source": {
      "source": "github",
      "repo": "anthropics/claude-plugins-official"
    },
    "installLocation": "/home/vscode/.claude/plugins/marketplaces/claude-plugins-official"
  }
}
MKJSON
    echo "Marketplace installed."
else
    echo "Marketplace already present, skipping."
fi

echo "=== Setup complete ==="
