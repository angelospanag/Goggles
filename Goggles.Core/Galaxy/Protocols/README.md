# Galaxy protocol definitions

Reverse-engineered protobuf definitions for GOG Galaxy's local protocols, copied unmodified from
[Yepoleb/gog_protocols](https://github.com/Yepoleb/gog_protocols) at commit
`923e8a0008603365d29002db611559190da5bf99` (2025-01-10). That repository is released under
**CC0 1.0** (public domain), so these files carry no licence obligations.

- `gog.protocols.pb.proto` — the `Header` every message starts with, and the reply extensions
  (`rseq`, `code`, …).
- `galaxy.protocols.communication_service.proto` — what a game's Galaxy SDK asks the local
  Communication Service for: auth, achievements, stats, leaderboards, play time, cloud saves.

They are compiled to internal C# types at build time by `Grpc.Tools` (messages only — Galaxy is
not gRPC). Keep them byte-for-byte as upstream so they can be re-synced; the upstream repository
has further files (overlay, peer-to-peer, web broker) to add here only when Goggles handles them.
