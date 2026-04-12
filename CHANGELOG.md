# Changelog

## 1.0.8

- limited the Communism-specific Neow override to runs where `Communism` is the only modifier so deck-building modifiers such as `Sealed Deck` and `Draft` keep their normal card-selection flow
- restored compatibility with other modifiers that populate or alter the starting deck through Neow, preventing empty-deck starts when those modifiers are combined with `Communism`

## 1.0.7

- audited the base-game gold write paths again so host-authored shared-gold sync now covers the current event, relic, power, potion, card, reward, shop, and treasure-room callsites from one code path
- stopped combat gold desyncs from drifting the checksum by tracking shared-pool totals separately while still keeping the source player's real combat gold in sync during theft and other in-combat gold changes
- switched direct shared-gold network messages to the live run location so room-entry relic payouts such as `Maw Bank` use a safer multiplayer timing

## 1.0.6

- split host-authored shared-gold replication by source so events, relics, powers, cards, treasure-room effects, and rewards no longer all reuse the same actor sync rule
- fixed event-driven shared-gold desyncs such as `Ranwid the Elder` by excluding or correcting the source player slot only when unmodded peers already applied the local gold change
- added richer gold sync logging to identify which gameplay source triggered each shared-gold update during multiplayer debugging

## 1.0.5

- centralized shared-gold gain/loss handling so event, relic, reward, shop, and combat gold changes follow one host-authored sync path
- deferred combat-time gold theft and stolen-back reward resolution until the safe post-combat sync window to stop `Gremlin Merc` desyncs
- limited `Scroll Boxes` to removing only the triggering player's starting gold instead of deleting the full shared pool

## 1.0.4

- replaced the unstable Neow prediction logic with a simpler host-authored initial shared-gold sync
- limited Neow-only catch-up gold replication so combat rewards and shop spending stop being double-applied
- refreshed the multiplayer gold flow for host-only installs and packaged the current hotfix build as `v1.0.4`

## 1.0.3

- simplified the shared-gold sync flow back toward the stable host-mirroring path to remove recent multiplayer regressions
- normalized shared gold on deterministic event exits so Neow no longer desyncs when only the host has the mod installed
- restored stable combat and reward progression after the Neow and shop hotfix series

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
