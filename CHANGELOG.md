# Changelog

## 1.0.2

- fixed multiplayer load/rejoin flow so a crashed or disconnected player can rejoin a shared-gold run without inheriting the placeholder modifier
- sanitized `ClientLoadJoinResponseMessage` save payloads before they are sent to clients

## 1.0.1

- fixed multiplayer clients skipping the Neow relic selection when only the host has the mod installed
- sanitized custom modifier data before lobby and run-start network messages are sent to remote players

## 1.0.0

- first public release of `Communism Mode`
- added the custom run modifier entry and shared-gold runtime
- fixed Neow so the starting relic choice still appears with the modifier enabled
- localized the custom modifier description for major STS2 interface languages
- added release packaging and repository documentation
