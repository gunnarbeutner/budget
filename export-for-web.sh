#!/bin/bash

# Export only the tables we need from MoneyMoney database to a plain SQLite file
# This avoids the LOCALIZED collation issue and creates a portable database

set -e

# Configuration
MONEYMONEY_DB="$HOME/Library/Containers/com.moneymoney-app.retail/Data/Library/Application Support/MoneyMoney/Database/MoneyMoney.sqlite"
EXPORT_DIR="$HOME/budget/data"
EXPORT_FILE="$EXPORT_DIR/moneymoney.db"
PASSWORD="nopasswd"
CIPHER_COMPAT=4

# Create export directory if it doesn't exist
mkdir -p "$EXPORT_DIR"

# Check if MoneyMoney database exists
if [ ! -f "$MONEYMONEY_DB" ]; then
    echo "Error: MoneyMoney database not found at $MONEYMONEY_DB"
    exit 1
fi

# Check if sqlcipher is installed
if ! command -v sqlcipher &> /dev/null; then
    echo "Error: sqlcipher is not installed. Install with: brew install sqlcipher"
    exit 1
fi

# Remove existing export if it exists
rm -f "$EXPORT_FILE"

# MoneyMoney keeps a lock on its live DB while it's running, which blocks even read-only
# PRAGMAs from sqlcipher. Copy the encrypted DB (and its WAL/SHM sidecars, if any) to a
# scratch location first so sqlcipher has exclusive access.
SCRATCH_DIR=$(mktemp -d -t moneymoney-export-XXXXXXXX)
trap 'rm -rf "$SCRATCH_DIR"' EXIT
SOURCE_COPY="$SCRATCH_DIR/source.sqlite"

echo "Copying MoneyMoney database to scratch location..."
cp "$MONEYMONEY_DB" "$SOURCE_COPY"
[ -f "${MONEYMONEY_DB}-wal" ] && cp "${MONEYMONEY_DB}-wal" "${SOURCE_COPY}-wal"
[ -f "${MONEYMONEY_DB}-shm" ] && cp "${MONEYMONEY_DB}-shm" "${SOURCE_COPY}-shm"

echo "Creating new plain SQLite database..."
sqlite3 "$EXPORT_FILE" "SELECT 1;" > /dev/null

echo "Exporting tables from MoneyMoney database..."
sqlcipher "$SOURCE_COPY" <<EOF
PRAGMA key = '$PASSWORD';
PRAGMA cipher_compatibility = $CIPHER_COMPAT;

ATTACH DATABASE '$EXPORT_FILE' AS export KEY '';

-- Export category_groups (defines category hierarchy)
CREATE TABLE export.category_groups AS SELECT * FROM category_groups;

-- Export categories
CREATE TABLE export.categories AS SELECT * FROM categories;

-- Export accounts
CREATE TABLE export.accounts AS SELECT * FROM accounts;

-- Export transactions
CREATE TABLE export.transactions AS SELECT * FROM transactions;

DETACH DATABASE export;
.quit
EOF

if [ -f "$EXPORT_FILE" ]; then
    echo "✓ Database exported successfully to: $EXPORT_FILE"
    ls -lh "$EXPORT_FILE"

    # Test the exported database
    echo "Testing exported database..."
    sqlite3 "$EXPORT_FILE" "SELECT COUNT(*) as categories FROM categories;" 2>/dev/null || echo "Warning: Could not verify categories"
    sqlite3 "$EXPORT_FILE" "SELECT COUNT(*) as transactions FROM transactions;" 2>/dev/null || echo "Warning: Could not verify transactions"
else
    echo "✗ Export failed"
    exit 1
fi

# Optional: ship the exported DB to a remote OpenBudget instance.
# Set both OPENBUDGET_UPLOAD_URL (e.g. https://your.server/api/moneymoney-db) and
# OPENBUDGET_UPLOAD_TOKEN (matching the server's MONEYMONEY_DB_UPLOAD_TOKEN env var).
if [ -n "$OPENBUDGET_UPLOAD_URL" ] && [ -n "$OPENBUDGET_UPLOAD_TOKEN" ]; then
    echo "Uploading to $OPENBUDGET_UPLOAD_URL..."
    if curl -fsS -X POST \
        -H "Authorization: Bearer $OPENBUDGET_UPLOAD_TOKEN" \
        -H "Content-Type: application/octet-stream" \
        --data-binary "@$EXPORT_FILE" \
        "$OPENBUDGET_UPLOAD_URL"; then
        echo
        echo "✓ Uploaded"
    else
        echo "✗ Upload failed"
        exit 1
    fi
elif [ -n "$OPENBUDGET_UPLOAD_URL" ] || [ -n "$OPENBUDGET_UPLOAD_TOKEN" ]; then
    echo "Warning: set both OPENBUDGET_UPLOAD_URL and OPENBUDGET_UPLOAD_TOKEN to enable upload (or neither for local-only)"
fi
