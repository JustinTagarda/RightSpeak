# PREMIUM-GATING-TEST-MATRIX

## Purpose

Minimal regression checklist for enforcing `BASIC-PREMIUM-GATING-POLICY.md`.

## Preconditions

- Test harness can control entitlement mode (`Basic` vs `Premium`) through fakes/mocks.
- Dialog assertions can verify that blocked actions surface Premium upgrade UX.
- Existing purchase/navigation route can be observed (in-app purchase first, Store fallback second).
- Premium entitlement resolver can be exercised with both Store license evidence and Store collection evidence.

## Basic Mode - Allowed

1. `Read` typed text succeeds.
2. `Read Selected Text` succeeds when retrieval path returns content.
3. `Read Document` succeeds when retrieval path returns content.
4. Theme changes are available and persisted.
5. Full status/error messaging remains visible.
6. Manage Voices window opens and catalog is viewable.

## Basic Mode - Blocked

1. Selecting a voice other than `System default` or `Ljspeech` is blocked.
2. Voice `Install` action is blocked.
3. Voice `Update` action is blocked.
4. Voice `Remove` action is blocked.
5. Hotkey modifier/key remap attempts are blocked.

## Premium Mode - Full Access

1. Voice selections are not gated.
2. Voice `Install`/`Update`/`Remove` are not gated.
3. Hotkey customization is not gated.
4. Typed read, selected-text read, and document read all remain available.

## Blocked Dialog Contract (Basic)

For each blocked feature path, assert:

1. Dialog title is `Premium feature`.
2. Dialog body explains action was blocked and Premium is required.
3. Primary button is `Upgrade to Premium`.
4. Secondary button is `Not now`.
5. Primary action routes to existing purchase flow and Store fallback behavior.

## Suggested Automated Test Cases

1. `Basic_Allows_ReadDocument`
2. `Basic_Blocks_NonAllowedVoiceSelection_ShowsPremiumDialog`
3. `Basic_Blocks_VoiceInstall_ShowsPremiumDialog`
4. `Basic_Blocks_VoiceUpdate_ShowsPremiumDialog`
5. `Basic_Blocks_VoiceRemove_ShowsPremiumDialog`
6. `Basic_Blocks_HotkeyCustomization_ShowsPremiumDialog`
7. `Premium_Allows_AllGatedFeatures`
8. `BlockedDialog_Upgrade_UsesExistingPurchaseRoute`
9. `PremiumResolver_LicenseSkuPrefix_MatchesConfiguredPremiumAddOn`
10. `PremiumResolver_WrongDurableAddOn_DoesNotUnlockPremium`
11. `DevelopmentMode_HidesFooterPremiumStatusAndUpgrade`
12. `ActivationRefresh_DetectsExternallyRedeemedPremiumEntitlement`

## Manual Smoke Additions

1. In Basic mode, open Manage Voices and verify browse works while install/update/remove are blocked with dialog.
2. In Basic mode, verify Read Document still works end to end on representative external targets.
3. In Premium mode, verify blocked dialogs do not appear for previously gated actions.
4. In a packaged Store build, redeem a Premium durable add-on promo code outside the app, reactivate the app, and verify Premium is detected after refresh.
5. In an unpackaged development run, verify footer `Basic/Premium` status text and `Upgrade` stay hidden.
