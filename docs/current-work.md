# Current work

## Owner and objective

The November 3 user direction is offline-first FO3/FNV implementation and tests
across the complete game systems. Root owns integration and publication.
Do not launch a campaign during this block. Recording stays off.
The full system checklist and acceptance boundaries are in implementation-plan.md.

The first later gameplay acceptance routes remain ordinary FNV New Game through
Goodsprings and departure, and standalone FO3 through Vault 101 to populated,
interactive Megaton. Neither route nor either complete campaign is accepted.
Flat and XR share authoritative gameplay and saves; physical XR remains open.

## Verified state

The integrated offline batch is published through PR352 from
codex/playable-opening-routes, based on main
adacd1a8f18f550e94083e58d4116b06d29d49aa.
The earlier palette decoder, recipe/quantity controls and actor retirement
repairs remain integrated. Their component checks passed; gameplay is unverified.

The offline repair batch fixes three concrete shared-owner defects:

- Expression arguments and values now admit complete digit-leading source IDs
  while preserving numeric literals, inactive evaluation and existing identifiers.
- Local declaration admission reads the exact compiled variable name despite
  trailing editor-text punctuation; missing slots and conflicting kinds refuse.
- Barter records actual weapon/ammunition transfer order, including ammunition
  carried by a loaded gun without an explicit ammunition trade row.

Focused script and reference contracts pass. Eight barter cases cover both
directions, implicit/explicit/split ammo, rollback and cold state. Owned FO3 and
FNV 10mm-pistol reload/trade checks pass with source magazine capacity, exact
ammunition conservation, instance identity, script-entry history and cold restore.

Complete selected-stack quest audits read 628,471 winning FNV records and
930,306 FO3 records. The FNV selection includes the installed diagnostic weapon
comparison plugin; it is not a pristine official-only selection. Source stack
identities are unchanged across the comparison, all raw program fields remain
accounted for, and both audits instantiate zero world references.

Unbound expression statements fall from 1,308 to 1,294 in FNV and 844 to 808 in
FO3. FO3 local-declaration failures fall from one to zero. FNV retains 12
source-parser and three compiled-extent findings; FO3 retains ten source-parser
and one compiled-extent finding. Empty-program stale metadata findings are
distinct from missing executable behavior. Statement dispatch remains uninspected
for 51,518 FNV and 28,988 FO3 statements. Neither audit establishes their effects.

Both broad readiness audits intentionally exit one: complete runtime readiness
is false. SCDA instruction execution, remaining source failures, all quest
branches, dialogue/combat/effects, housing CELL ownership and complete cold
continuation remain open. Passing selected tests is not whole-game acceptance.

Hardcore needs and Sleep/Wait follow a clean-room behaviour description.
Dehydration, Hunger and SleepDeprivation accrue in real time from the effective
rate settings (saved in vitals, campaign schema v74+). Their DEHY/HUNG/SLPD stage
diseases, notices and fatal damage come from the records. Need effects of food,
water and the Doctor's Bag apply, and the HUD and Pip-Boy show the needs.
Sleep/Wait uses the original refusal chains and runs one game hour per real
second. The hour step order is the original one, and the full heal happens only
when a non-Hardcore Sleep ends naturally. Synthetic and owned-data contracts pass;
the e2e rest test waits three hours in the world.
Trespass outside unowned cells, alarm, underwater and radiation admission
inputs, the HealRate/Modify Recovered Health heal terms, rest autosave/fade
and actor catch-up remain unbound and are named in rest telemetry.

Combat damage now follows one ordered owner: the attacker formula (attack-arm,
skill, MeleeDamage/UnarmedDamage, companion Charisma), then target mitigation
(DR capped by fMaxArmorRating, DT with entries 58/56, ammo and entry-0 damage,
the minimum floor, weapon resistance type, VATS player multiplier) and the
per-mille critical roll with entries 1/2/36. Critical stages, SetVATSTarget and
the Agility PushActorAway force are owned and saved (schema v75). Still open and
visible: critical effect spells (no actor active-effect owner), ash piles,
Meltdown, sneak-attack detection, blocking, split beam, disarm/stagger, scripted
FireWeapon, flee destinations, the player ragdoll knockdown and VATS special moves.

## Next owner and publication

The complete required runtime gate passes: Release/Debug, formatting/analyzers,
contract probes, native plugin domains, launcher and headless Godot components.
Diff review and Release export pass. The local executable is
tmp/development-runtime/windows/OpenNV.exe; export did not launch a campaign.
Root owns checked PR352 publication, merge and local/origin main equality.
The latest user instruction makes offline verification the publication scope;
do not imply that merge accepts either opening route or whole-game parity.

Private current audit rows are under local/offline-capabilities-current-20261103.
Keep only the compact baseline comparison and the active current diagnostics,
not duplicate audit trees. No owned files, saves, binaries or derivatives belong
in the PR. No campaign has been launched during this block.

After checked publication, the next implementation owner is the compiled-script
and remaining source-admission gap, using the retained exact failures. FO3-specific
weapon damage/repair rules, broader effects and housing access remain visible
additional gaps. Actual ordinary-input opening-route and fresh-process save
continuation follow the offline block; matched retail evidence is additionally
required before parity claims.

## Script command coverage

Shared object, quest, effect and result runs map an unresolved reference
qualifier to the engine's three outcomes: a statement reuses the previous or
implicit receiver, an expression term is zero, a quest statement without any
receiver abandons that run (pending frame events survive), and a non-reference
expression qualifier disables the script form for the session. A zero ref
variable keeps its shared last resolved form. Each degraded site reports one
counted OPENNV_SCRIPT_QUALIFIER line.

EnterTrigger runs the trigger script at once (nesting limit five), ClearOwnership
and SetCellImageSpace are saved in schema v74, script actor alpha and shaders
follow loaded 3D, NPC CastImmediateOnSelf applies value effects, and OnOpen and
OnClose dispatch when door motion finishes. ScriptCommandContracts covers these.

Still visibly deferred: PlaceLeveledActorAtMe and PlaceAtReticle (no runtime
actor spawner or camera pick owner), SetLevel, SetPackDuration, Reset3DState,
base-record SetItemValue, NavMesh exclusion, SetScreenSplatterFade, the AddSpell
HUD notice and ability application, GetLineOfSight native rays, securitron faces,
shader particles, ShowTutorialMenu, SetPCCanUsePowerArmor, readers of the
SetNoAvoidance/SetAllVisible flags, and the OnStartCombat, OnReset,
ScriptEffectUpdate, OnDestructionStageChange, OnMagicEffectHit, OnSell and
OnActorEquip events.

## AI package coverage

All PKDT general flags are admitted by every package reader; flags without a
runtime owner print one OPENNV_PACKAGE_DEFERRED line instead of refusing the
actor's AI. Behaviour flags are inert and type-specific flags are masked to the
procedure's own bits. Must Complete holds only procedures with a Done owner and
Must Reach only those with an arrival owner, against lower-priority selections,
until done, the schedule ends or the package's conditions lapse. Once Per Day
records the completion day. Door flags lock or unlock the package location's
interior CELL doors; script-changed locks are left alone (saved, schema v77).

NPC Wander, UseItemAt, Ambush, FleeNotCombat and UseWeapon run as saved step
lists over the shared movement, activation, combat-start and event owners;
missing-target packages and CastMagic run as Travel. PackageStepContracts covers
admission and every step transition. Still visibly deferred and holding (never
completing): UseWeapon discharge, melee, equip and reload; UseItemAt sitting,
use idles and consumption; ambush override radius and victim search; heading
alignment; locations in another navigation region; and creature dispatch of
these five types. No ordinary-play run has yet exercised them.
