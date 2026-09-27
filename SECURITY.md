# Security and Privacy

This document describes the security and privacy boundaries of Avatar Part Assembler (APA).

## Current privacy status

The current default branch should not contain workstation-specific absolute paths, user-profile directories, local harness/run directories, API keys, passwords, or private signing material.

Repository-relative paths such as `Editor/...`, `Runtime/...`, `Assets/...`, `Packages/manifest.json`, and `Library/ScriptAssemblies` are project structure references and are not personal workstation paths.

### Git history caveat

Previously committed documentation contained local workstation path information. Those strings have been removed from the current tree, but old Git commits remain readable unless repository history is rewritten.

If complete removal from repository history is required, the affected history must be rewritten and the remote refs force-updated. This changes commit SHAs and requires existing clones to rebase, re-clone, or otherwise reconcile rewritten history.

## Protected Mesh threat model

Protected Mesh mode is a distribution format, not a strong DRM or anti-tamper security boundary.

It provides:

- removal of the direct source Mesh / model-file dependency from the distributed prefab;
- encryption of the serialized mesh payload at rest;
- integrity checks that reliably detect accidental corruption and modifications for which the authentication tag was not recomputed;
- fail-closed decoding for malformed, truncated, unsupported, wrong-part, or inconsistent payloads;
- bounded decoder counts and lengths before major allocations;
- transient in-memory hydration rather than writing the decoded Mesh back into the project.

It does **not** provide:

- confidentiality against a determined recipient who can inspect the shipped plugin;
- authenticity against a recipient who can reproduce the package's key-derivation process;
- prevention of mesh extraction from Editor memory;
- protection of materials, textures, bones, animations, or other normally referenced Unity assets.

The current codec derives encryption and HMAC material from a package-local derivation value that ships with the plugin. Therefore, anyone able to inspect the plugin can reproduce the derivation process and construct a payload with a valid authentication tag. The integrity check must not be described as protection against a malicious recipient.

A stronger authenticity design would require a trust root that is not distributed with the decoder, such as author-side signing with a private key and verification with a public key. Confidentiality from the recipient is fundamentally limited because the recipient's Editor must be able to decode the mesh in order to build the avatar.

## Resource-exhaustion boundary

The protected payload decoder validates counts and lengths before allocations, but the current maximum plaintext size is large enough that a deliberately oversized valid-format asset can still create substantial Editor memory pressure.

In particular, authentication and decryption may require additional buffers near the payload size. This is a bounded resource-exhaustion / denial-of-service risk, not an arbitrary-code-execution issue.

Future hardening should consider streaming authentication/decryption and/or a lower practical payload limit based on real avatar-part sizes.

## Authoring-path safety

APA authoring output paths are restricted to project-relative Unity asset paths. Absolute filesystem paths, UNC paths, parent traversal segments, invalid filename characters, and output paths outside `Assets/` are refused rather than rewritten.

Existing assets are not silently overwritten. Writes distinguish between creating a new asset, explicitly replacing an APA-owned asset, and refusing a path occupied by an unrelated asset type.

## GitHub Actions supply-chain notes

The release workflow:

- grants `contents: write` only because the release job must publish GitHub Releases;
- uses commit-pinned third-party actions rather than movable tags;
- disables persisted checkout credentials after checkout;
- packages tracked repository files and removes `.github` from the distributed package staging directory.

Maintainers should review pinned action SHAs before updating them.

## Secrets and credentials

Do not commit:

- API tokens, PATs, OAuth credentials, webhook secrets, signing private keys, or passwords;
- local environment files containing credentials;
- private package-registry credentials;
- crash logs or diagnostic dumps that contain local usernames, project paths, or account identifiers.

Use GitHub repository / environment secrets for CI credentials when a future workflow requires them.

## Issue and log privacy

When reporting a bug, prefer:

- APA version and dependency versions;
- stable `APAxxx` diagnostics and `reason=...` tokens;
- repository-relative asset paths only when necessary;
- a minimal reproduction project with unrelated assets removed.

Before posting logs publicly, remove absolute filesystem paths, usernames, machine names, account identifiers, private asset names, and unrelated project content.

## Reporting a security issue

If a report would expose a secret, private asset, unpublished exploit, or personal information, do not post it in a public issue. Use a private repository security-reporting channel when one is configured, or contact the maintainer privately through an existing trusted channel.
