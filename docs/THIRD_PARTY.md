# Third-party work: what was read, what was taken, and the rule

[Русская версия](ru/THIRD_PARTY.md)

This project has never been run in a real game. Every defect that only running
reveals has therefore been found by reading projects that *have* been run. That is
a legitimate and ordinary way to build software, and it is also the part of a
project most likely to be done carelessly — so this document records exactly what
was read, under what licence, and what came out of it.

## The rule, in one line

**Copyright protects expression, not ideas.** Reading how somebody solved a
problem and then solving it yourself is legal and normal. Copying their code is
not, and no permission from this project's owner can change that, because the code
belongs to its authors and not to us.

That line is real but it is not a licence to read anything at all. The practical
policy here is graded by risk:

| What | Policy | Why |
| --- | --- | --- |
| Commit messages, issue trackers, release notes, forum posts | **Free, any project, any licence** | A bug report is a statement of fact about a game's behaviour. No copyright attaches to "the radio desynchronises when the station is missing" |
| Facts about GTA V's own API — native names, argument orders, bone and Euphoria part indices, that stealth movement is what a crouch is | **Free, from anywhere** | Facts about a third party's engine are not the discovering project's property |
| Permissively licensed source (MIT, BSD, Apache) | **Read freely.** Copy only with the licence text and attribution carried along | Copying is *allowed*; we still prefer our own implementation, because a borrowed one is not understood |
| Copyleft source (GPL, AGPL, LGPL) | **Metadata only by default.** Read implementation only when there is a specific question nothing else answers, write down the fact learned, then implement from the note | Reading is lawful, but a close reading followed by structurally similar code is how an independent implementation stops being independent. See below |
| Leaked or reposted proprietary source | **Never opened** | Not a licensing question. Reading it taints every line written afterwards |

## Why copyleft is handled more carefully than "ideas are free" suggests

The idea/expression line is not a bright one in practice. Courts assess substantial
similarity by abstraction–filtration–comparison, which looks past literal text at
structure, sequence and organisation. Code written immediately after a close
reading of someone else's tends to inherit theirs — the same helper split, the same
order of operations, the same names — and at that point "we wrote it ourselves" is
a claim that is hard to support.

The industry answer is clean-room design: one person reads and writes down *what*
the thing does, another implements from that description. A one-person project
cannot run a real clean room, so the approximation used here is to keep the reading
and the writing separated by an artefact — a written statement of the fact learned,
which is what gets implemented from.

Where that has been done, the commit says so.

## What was actually read

### RAGECOOP-V — MIT, read in full

<https://github.com/RAGECOOP/RAGECOOP-V>

Cloned and read as source, which the MIT licence permits without reservation. Its
authors could be copied from with attribution; nothing was, because an
implementation you did not write is one you cannot maintain.

What came out of it:

- **The weapon defect.** Their commit *"Fix network players never switching back to
  unarmed"* is the narrow version of a bug we had in full: no weapon was applied to
  a remote ped at all. The commit message was the clue; the fix is ours, and covers
  the holster case theirs had missed.
- **The vehicle break/repair loop.** They hit a flicker and removed their repair
  branches (*"causes break/repair loop in some situations"*). Knowing the failure
  mode let us keep the repair direction and avoid the loop a different way, by
  comparing against the previous *report* rather than against the game.
- **The ragdoll technique.** Correcting a small set of bones with Euphoria impulses
  rather than replicating a skeleton. The idea is theirs; the constants, the
  settle delay, the give-up distance and the code are ours.

Euphoria part indices (head 20, right foot 6, left foot 3) are facts about GTA V,
not about RAGECOOP.

### GTACoOp and oldnapalm's fork — GPL-3.0, metadata only

<https://github.com/Guad/GTACoop> · <https://github.com/oldnapalm/GTACoOp>

The ancestor of this whole family of mods, and still maintained. It is GPL-3.0, so
its code cannot enter this project.

It was cloned with `--no-checkout --filter=blob:none`, which puts commit metadata
on disk and no source file at all — the discipline is enforced by the clone, not by
intention. What was read is 627 commit subjects.

What came out of it: **the radio station defect.** Their history records *"Fix Sync
error if radiostation doesn't exist"*. We had the same latent bug and worse — the
field was replicated and applied by nothing — and the fix here resolves the station
index to a *name* per client, which is our answer to a problem their message named.

### TwoPlayerMod — GPL-3.0, not used

<https://github.com/BenjaminFaal/TwoPlayerMod>

Read about, not read. It drives a second ped from a gamepad, which is the local
input problem; every ped here is driven from replicated state. Nothing to learn
that applies.

### LSPDFR+ — GPL-3.0, one fact confirmed

Consulted only to confirm that `Functions.OnOnDutyStateChanged += handler` with a
`void(bool)` handler is how the event is subscribed in practice. That is a fact
about LSPDFR's API, not LSPDFR+'s expression. Its licence would not permit code
into this project in any case.

### FiveM / CitizenFX — not usable, and not read

<https://github.com/citizenfx/fivem>

Frequently described as open source and MIT. It is neither: the repository states
that use is subject to the Rockstar Games Creator Platform License Agreement, with
some files under LGPL, and the project now belongs to Rockstar. Nothing from it can
enter this project under any arrangement.

Repositories titled *"Source code of GTA V multiplayer named FiveM (MultiFive)"* are
reposted leaks of that code — one of them sits under an account named for a cheat
vendor. None was opened.

### GTA V's own source code — declined

Offered and refused. It is a leak; reading it would taint everything written
afterwards and would make the project undistributable in a way no licence could
repair.

### LSPDFR itself — signatures only

Public type and member signatures were enumerated from the shipped assembly's
metadata: the same surface `System.Reflection` sees at runtime, which is how this
project binds to LSPDFR at all. Method bodies were not decompiled and the `.pdb`
was not read. A licence template purporting to authorise decompilation was declined
— only its rights holder can grant that, and the template did not come from them.

No third-party file is committed to this repository.

## What this document is for

If somebody asks where a piece of this project came from, the answer should exist
in writing before the question does. That is the whole purpose. A project that
cannot say what it read is one that will eventually be assumed to have copied.
