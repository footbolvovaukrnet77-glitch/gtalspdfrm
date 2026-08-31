# LSPDFR integration

## Status

| Capability | State |
| --- | --- |
| Detection and version reporting | **Working** |
| Callout plugin enumeration | **Working** |
| Registration points (state keys, network event) | **Working** |
| Callout, pursuit, suspect and police-AI replication | Phase 8 |

## LSPDFR is not required

LSPDFR is an RPH plugin, so it inherits everything in
[RPH_INTEGRATION.md](RPH_INTEGRATION.md) and adds one problem of its own.

The adapter lives in its own assembly, loaded only when LSPDFR is detected. The
Multiplayer Core has no knowledge of it. Nothing in the core references anything
police-related — that separation is the point of the adapter model.

## Why reflection, specifically

LSPDFR ships no redistributable SDK. `LSPD First Response.dll` is installed with
the mod and its licence does not permit redistributing it, so this adapter
**cannot reference it at compile time** — there is no package to reference, and
vendoring the DLL would be a licence violation.

Everything it reads is therefore bound by name at runtime through
`ReflectionProbe`, which records every lookup that misses and surfaces the count
through `/diagnostics` instead of throwing.

**The cost, stated:** reflection binds by name, so a rename inside LSPDFR turns
into a runtime miss rather than a compile error. That is why misses are counted
and reported rather than ignored.

## The second problem

Beyond RPH's host isolation, LSPDFR's public API surface is not versioned. Its
`API.Functions` class is the supported entry point, but the state a multiplayer
framework actually needs — live callout instances, pursuit membership, suspect
role assignment — is behind internals that change between releases.

Binding to those internals by name would produce an integration that works
against one LSPDFR build and breaks on the next, silently, in the middle of a
callout.

**Chosen route for Phase 8:** an LSPDFR-side plugin, loaded by LSPDFR itself,
consuming the supported `API.Functions` surface and the public event hooks, and
publishing state across the same in-process channel Phase 7 builds. Not reflection
into internals.

**Compromise:** only what `API.Functions` exposes can be replicated. Some callout
internals will not be reachable, and those will be listed here as they are found —
not worked around by reaching into private state.

## Registration points available today

The adapter declares these now so mod authors can use them and so `/entity` can
describe them:

| Key | Meaning |
| --- | --- |
| `lspdfr.callout` | Identifier of the callout an entity belongs to |
| `lspdfr.role` | `suspect`, `victim`, `witness`, `officer` |
| `lspdfr.pursuit` | Identifier of the pursuit an entity is part of |
| `lspdfr.arrested` | Set when a suspect has been arrested |

Plus the `lspdfr.event` network event for opaque payloads.

These are already replicated and persisted: `CustomData` travels with the entity
in every delta, and is stored verbatim, even on a server that has never heard of
LSPDFR.

## Planned scope for Phase 8

From master prompt section 18, in the order they will be attempted:

1. Callout lifecycle — start, state, objectives, completion, failure, cancellation
2. Callout participants — suspects, victims, witnesses, with roles
3. Pursuits — membership, state, and the ending
4. Traffic stops and arrests
5. Police units, backup and emergency-vehicle state (lights, sirens)
6. Warrants and evidence
7. Officer state and police AI

Each will be listed here with what was achievable through `API.Functions` and what
was not, as it is built.
