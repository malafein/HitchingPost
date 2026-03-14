#!/bin/bash

# Configuration
PROJECT_NAME="HitchingPost"
DLL_PATH="bin/Debug/net462/$PROJECT_NAME.dll"
STAGING_DIR="staging"

echo "Deploying $PROJECT_NAME locally..."

# 1. Build project
echo "Building project..."
dotnet build -c Debug || { echo "Build failed!"; exit 1; }

# 2. Update staging directory
echo "Staging files..."
mkdir -p "$STAGING_DIR"
cp "$DLL_PATH" "$STAGING_DIR/"
cp README.md "$STAGING_DIR/"
cp CHANGELOG.md "$STAGING_DIR/"
cp manifest.json "$STAGING_DIR/"
if [ -f "Assets/icon.png" ]; then
    cp Assets/icon.png "$STAGING_DIR/"
elif [ -f "icon.png" ]; then
    cp icon.png "$STAGING_DIR/"
fi

# 3. Local Deployment
PLUGINS_DIR="/home/malafein/.config/r2modmanPlus-local/Valheim/profiles/Testing/BepInEx/plugins/malafein-$PROJECT_NAME"
echo "Deploying to r2modman Testing profile: $PLUGINS_DIR..."
mkdir -p "$PLUGINS_DIR"
cp -r "$STAGING_DIR"/* "$PLUGINS_DIR/"

echo "Done! Deployed to local plugins."
