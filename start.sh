#!/usr/bin/env bash
set -e

# Local development startup script
# For Home Assistant addon deployment, use addon/run.sh instead

cd "$(dirname "$0")"

# Set development environment variables
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://localhost:5000"
export ConnectionStrings__DefaultConnection="Data Source=temporarylinks.db"

# Optional: Set these for local testing with Twilio
# export Twilio__AccountSid="your_account_sid"
# export Twilio__AuthToken="your_auth_token"
# export Twilio__PhoneNumber="+1234567890"

# Optional: Set for local HA testing (get a long-lived token from HA)
# export HomeAssistant__BaseUri="http://localhost:8123/api/"
# export HomeAssistant__Token="your_long_lived_access_token"

echo "Starting Temporary Links in development mode..."
echo "URL: http://localhost:5000"

cd src/TemporaryLinks.Addon
dotnet run
