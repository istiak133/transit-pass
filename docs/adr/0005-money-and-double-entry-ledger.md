# 0005. Money representation and double-entry ledger

- **Status:** Accepted
- **Date:** 2026-11-04

## Context
The original system stored a mutable `Wallet.Balance` and changed it with read-modify-write in EF Core. Two concurrent
purchases both read 100, both subtract 80, and the last write wins: two tickets for 80 taka (PRD P2). Nothing recorded
*why* a balance changed, so errors could not be traced or reversed.

## Decision

**Representation.** Money is a `Money` value object holding a `long` count of **minor units (poisha)** plus a currency
code (`BDT`). Columns are `bigint`. Arithmetic is integer, so there are no rounding surprises and no `decimal` scale mismatches between C# and SQL.

**Ledger.** Every movement is a `ledger_transaction` with two or more `ledger_entries` whose amounts sum to zero:

```
accounts(id, kind, owner_id, currency, balance_minor NULL, floor_minor, version)
ledger_transactions(id, kind, reference UNIQUE, created_at)
ledger_entries(id, transaction_id, account_id, amount_minor, created_at)   -- immutable
```

| Operation | Entries |
|---|---|
| Top-up | `GatewayClearing −x`, `PassengerWallet +x` |
| Ticket purchase / fare | `PassengerWallet −x`, `FareRevenue +x` |
| Penalty | `PassengerWallet −x`, `PenaltyRevenue +x` |
| Refund | Reversal of the original entries, referencing it |

**Atomic debit.** The passenger balance projection changes in one statement, inside the same DB transaction that inserts
the entries:

```sql
UPDATE wallet.accounts
SET    balance_minor = balance_minor - @amount, version = version + 1
WHERE  id = @account_id AND balance_minor - @amount >= @floor
RETURNING balance_minor;
```

Zero rows returned means insufficient funds, decided by the database under its row lock. No application-level lock and no
retry loop. `@floor` is `0` for purchases and entry checks, and `-overdraft_limit` for exit charges (ADR 0010).

**No projection on system accounts.** Revenue and clearing accounts receive an entry on *every* tap. A projected
balance there would be one hot row that serializes all traffic. Their `balance_minor` is `NULL`, and their balance is
`SUM(amount_minor)` (or a periodic snapshot). Only passenger accounts, which see low per-row contention, carry a projection.

**Lock ordering.** A transaction that updates two projected accounts (a future family transfer) updates them in
ascending `id` order, which removes the classic A→B / B→A deadlock.

## Alternatives considered
- **Mutable balance only (original design).** It cannot be audited, cannot be reversed, and races.
- **Balance derived purely from `SUM(entries)` for passengers too.** Always consistent, but "can I afford this?" becomes
  an aggregate under a range lock on every purchase. The projection plus conditional update is O(1) and race-free.
- **Optimistic concurrency (`xmin`) with retry.** Correct, but under contention (one user double-tapping) it turns
  into retry storms. The conditional update never needs a retry.
- **`decimal(18,2)`.** Workable, but `long` minor units are the industry norm (Stripe, Adyen) and remove a class of scale bugs.

## Consequences
- Invariant I1 is enforced in code and checked by a reconciliation query in tests:
  `SELECT transaction_id FROM ledger_entries GROUP BY 1 HAVING SUM(amount_minor) <> 0` must return nothing.
- Projection drift is detectable: `balance_minor = SUM(entries)` per passenger account is a test assertion and a nightly check.
- Corrections are new transactions. History is never rewritten.
