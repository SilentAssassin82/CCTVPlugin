# Copilot Instructions

## Project Guidelines
- In the CCTVPlugin project, SendMessageToOthers (MESSAGE_ID 12346) has been a recurring source of bugs — it broadcasts GOTO/INDEX messages to ALL clients including the player, which interferes with cockpit seat transitions and causes other issues. Always use SendMessageTo targeting only the SpectatorSteamId instead.