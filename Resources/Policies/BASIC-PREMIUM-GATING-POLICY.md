# BASIC-PREMIUM-GATING-POLICY

## Purpose

This document is the repository source of truth for runtime feature gating between `Basic` and `Premium`.

Primary goal:
- prevent accidental entitlement regressions
- preserve core utility reliability in `Basic`
- ensure `Premium` remains full access

## Scope

- Applies to Microsoft Store packaged runtime behavior.
- Applies to feature-access decisions in UI, commands, service logic, tray actions, hotkey customization paths, and voice-management paths.
- Applies to implementation and test coverage expectations for gating behavior.

## Entitlement Source

- Use the existing Microsoft Store durable add-on entitlement flow already implemented in the app.
- Use existing entitlement refresh/cache behavior and purchase/navigation services.
- Do not introduce alternate entitlement systems unless explicitly approved.

## Policy Rules

### Basic Mode: Allowed Features

- Read typed/manual input text.
- Read selected text from external apps.
- Read document from external apps.
- Stop, pause, and resume playback.
- All currently available themes.
- Full status and error messaging (no reduced/error-suppressed messaging mode).
- Open Manage Voices window and browse available voice models.

### Basic Mode: Voice Access Rules

- Allow only:
  - `System default`
  - `Ljspeech` (when installed/available)
- Any selection/use attempt for other voices must be blocked by Premium gating UX.
- If `Ljspeech` is unavailable, `System default` remains available and behavior must fail gracefully.

### Basic Mode: Gated/Blocked Features

- Voice selection beyond `System default` and `Ljspeech`.
- Voice install actions in Manage Voices.
- Voice update actions in Manage Voices.
- Voice remove actions in Manage Voices.
- Hotkey customization (modifier/key remapping).
- Default hotkeys remain usable.

### Premium Mode: Access

- Premium has full access to all available app features.
- No feature gating in Premium mode.

## Blocked Action UX (Mandatory)

Every blocked Basic-gated action must present a consistent dialog.

Required dialog:
- Title: `Premium feature`
- Body: concise reason stating what was blocked and that Premium is required
- Primary button: `Upgrade to Premium`
- Secondary button: `Not now`

Primary action behavior:
- use the existing in-app Premium purchase route
- if in-app purchase is unavailable/not supported, use existing Store navigation fallback

Implementation note:
- Do not introduce a separate purchase path for blocked dialogs.
- Reuse already implemented purchase/navigation routing services.

## Consistency Requirements

- Apply identical gating semantics across all trigger surfaces:
  - main window actions
  - Manage Voices actions
  - tray paths that map to gated capabilities
  - hotkey-configuration surfaces
- Do not silently drop blocked actions.
- Blocked actions must not execute partially.

## Reliability Guardrails

- Do not gate or degrade core reading reliability paths in `Basic`.
- Do not alter established retrieval/speech reliability baselines as part of gating changes.
- Preserve existing diagnostics needed to troubleshoot runtime failures.

## Logging and Diagnostics Expectations

- Emit structured diagnostics for blocked-gate decisions with:
  - action identifier
  - mode (`Basic`/`Premium`)
  - gating reason
- Do not log selected text contents or other sensitive user content.

## Default Rule for New Features

- New features default to `Premium` only unless explicitly approved for `Basic`.
- Any approved exception must be added to this document in the same change.

## Regression Test Expectations

At minimum, policy regression coverage must verify:

1. Basic allows:
- typed read
- selected-text read
- document read
- theme switching

2. Basic blocks:
- non-allowed voice selection
- voice install/update/remove
- hotkey customization

3. Premium allows full access:
- all voice selections
- voice install/update/remove
- hotkey customization
- all core read flows

4. Blocked action UX:
- blocked paths show required Premium dialog
- dialog upgrade path uses existing purchase route and fallback

## Change Control

- Any intentional gating policy change must include:
  - update to this file
  - corresponding test updates
  - explicit note in PR/implementation report describing what changed and why
