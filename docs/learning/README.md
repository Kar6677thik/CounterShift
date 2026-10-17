# Learning notes

One note per milestone, written when the milestone is finished. Read them in order. Each one follows the same shape:

1. **What** we built, with links to code.
2. **Why** this way, and the alternatives rejected (deeper reasoning lives in the ADRs).
3. **Why now**: why this milestone sits at this point in the sequence.
4. **Concepts** introduced, with Field Guide section numbers.
5. **Commands** you can run to see it yourself.
6. **What broke / surprised us**, recorded honestly.
7. **Self-check**: questions you should now be able to answer without looking.

| # | Milestone | Batch |
|---|---|---|
| [00](00-scaffolding.md) | Scaffolding, build discipline, CI | A |
| [01](01-domain-bucketing-and-rules.md) | Domain: bucketing, decomposition, rules, property tests | A |
| [02](02-v0-scattered.md) | v0-scattered: the honest first version (tag `v0-scattered`) | A |
| [03](03-lost-updates-and-atomic-increments.md) | Break B1/B5: lost updates and deadlocks → atomic upsert | A |
| [04](04-branch-by-abstraction.md) | Break B2 + branch by abstraction: storage behind a seam | B |
| [05](05-redis-backend.md) | The new backend: Redis hashes, Lua, native expiry; why a naive switch is dangerous | B |
| [06](06-migration-machinery.md) | Migration machinery: dual writes, shadow parity, split, backfill, live reload | B |
