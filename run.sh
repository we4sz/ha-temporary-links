#!/usr/bin/env bash
set -e

CONFIG_PATH=/data/options.json

# Extract configuration values from Home Assistant addon options
TWILIO_ACCOUNT_SID=$(jq -r '.twilio_account_sid' $CONFIG_PATH)
TWILIO_AUTH_TOKEN=$(jq -r '.twilio_auth_token' $CONFIG_PATH)
TWILIO_PHONE_NUMBER=$(jq -r '.twilio_phone_number' $CONFIG_PATH)
DEFAULT_MESSAGE_TEMPLATE=$(jq -r '.default_message_template' $CONFIG_PATH)
LOG_LEVEL=$(jq -r '.log_level' $CONFIG_PATH)

# Export environment variables for the .NET application
export Twilio__AccountSid="$TWILIO_ACCOUNT_SID"
export Twilio__AuthToken="$TWILIO_AUTH_TOKEN"
export Twilio__PhoneNumber="$TWILIO_PHONE_NUMBER"
export HomeAssistant__DefaultMessageTemplate="$DEFAULT_MESSAGE_TEMPLATE"
export Logging__LogLevel__Default="$LOG_LEVEL"

# SUPERVISOR_TOKEN is automatically provided by Home Assistant
export HomeAssistant__BaseUri="http://supervisor/core/api/"
export HomeAssistant__Token="$SUPERVISOR_TOKEN"

# Database path (persisted in /data). Match the tuned fallback in Program.cs
# (Default Timeout=30;Pooling=false) so production runs with the same SQLite
# settings that prevent 'database is locked' dropped writes.
export ConnectionStrings__DefaultConnection="Data Source=/data/temporarylinks.db;Default Timeout=30;Pooling=false"

# Ingress configuration
export ASPNETCORE_URLS="http://0.0.0.0:8099"

echo "Starting Temporary Links addon..."
echo "Log level: $LOG_LEVEL"
echo "Twilio configured: $([ -n "$TWILIO_ACCOUNT_SID" ] && echo 'yes' || echo 'no')"

# Start the .NET application
exec dotnet /app/TemporaryLinks.Addon.dll
