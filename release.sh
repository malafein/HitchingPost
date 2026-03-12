#!/bin/bash

# Configuration
PROJECT_NAME="HitchingPost"
DLL_PATH="bin/Debug/net462/$PROJECT_NAME.dll"
STAGING_DIR="staging"
RELEASES_DIR="Releases"

# Get version from csproj
VERSION=$(grep -oPm1 "(?<=<Version>)[^<]+" "$PROJECT_NAME.csproj")
ZIP_NAME="${PROJECT_NAME}_v${VERSION}.zip"

echo "Releasing $PROJECT_NAME v$VERSION..."

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

# 3. Create release zip
echo "Creating zip: $ZIP_NAME..."
mkdir -p "$RELEASES_DIR"
cd "$STAGING_DIR" || exit 1
if command -v zip &> /dev/null; then
    zip -r "../$RELEASES_DIR/$ZIP_NAME" .
elif command -v 7z &> /dev/null; then
    7z a -tzip "../$RELEASES_DIR/$ZIP_NAME" . > /dev/null
else
    echo "Warning: Neither 'zip' nor '7z' command found. Skipping zip creation."
fi
cd ..

echo "Done! Release created at $RELEASES_DIR/$ZIP_NAME"
