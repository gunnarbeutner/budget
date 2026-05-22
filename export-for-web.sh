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

echo "Creating new plain SQLite database..."
sqlite3 "$EXPORT_FILE" "SELECT 1;" > /dev/null

echo "Exporting tables from MoneyMoney database..."
sqlcipher "$MONEYMONEY_DB" <<EOF
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
