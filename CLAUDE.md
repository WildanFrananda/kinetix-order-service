# CLAUDE.md — kinetix-order-service

The workspace `AGENTS.md` holds the stack and the verification commands. This file holds decisions
that the code states but cannot explain.

## Order items are priced by pricing

`OrderService.PricedItems` builds each item from pricing's line for it — the final unit price and line
total the buyer is charged, flash sale included — and the title from catalog. Nothing on an item comes
from the cart. A line pricing did not price, a quantity it priced differently, or lines that do not add
up to its subtotal refuse the checkout. Returns are refunded from these amounts, so they must be what
was paid.

## Shipping is quoted for the parcel, on the route it travels

Checkout asks matching for the courier options from the merchant's store, the point identity holds for it,
to the address the buyer typed, which identity's geocoder places (`geo.v1.GeocodingService/GeocodeAddress`),
for the parcel's weight: catalog's `weight_grams` times each line's quantity. A product with no weight refuses
the checkout (`PRODUCT_WEIGHT_UNKNOWN`), as does a store with no point (`MERCHANT_STORE_NOT_ON_MAP`) and an
address identity cannot place or places no closer than a suburb (`SHIPPING_ADDRESS_NOT_PLACED`, with the
reason); all three are 422. A geocoder that cannot answer is 503 `ADDRESS_LOOKUP_UNAVAILABLE`, never a bad
address. The order stores the drop-off point and the parcel weight, and the courier is dispatched to that
point with that weight.

Until 2026-10-18 the quote was asked with no route and no weight, so matching answered each tier's floor
(`TIER_FLOOR`): every buyer paid the minimum fee whatever the distance, and the courier went to the point on
the buyer's profile rather than the address on the order. `TIER_FLOOR` stays in the enum for the orders that
were quoted that way, and such an order, with no drop-off stored, is still dispatched to the profile point
with weight 0, which matching reads as unstated.

The geocode call has its own deadline, `KINETIX_GEOCODE_DEADLINE_SECONDS`, required: identity gives the
geocoding provider eight seconds, longer than the five the rest of the saga allows.

## A packed order is owed a courier

When warehouse reports an order packed, order records a row in `courier_dispatches` before it asks
anyone anything, then tries once: the store's point from identity and the drop-off stored at checkout,
`DispatchCourier` to matching, and the tracking number to warehouse. Whatever does not go through is retried by
`CourierDispatchSweeper`, backing off a minute per attempt up to an hour, the same shape as
`shipping_settlements`. Until 2026-10-11 a failed dispatch was a log line and nothing else: the order sat
in `PROCESSING_FULFILLMENT` with no courier for good, and when identity could not be asked the log said
the address had never been placed.

Retrying is safe because matching books one job per order number and answers a repeat with the same
dispatch and tracking number. `NO_DRIVER_AVAILABLE` is retried too — matching assigns the job itself when
a courier comes online, and the retry is how order learns the tracking number to give warehouse. A
dispatch that went through but whose tracking number warehouse could not be told retries only the
warehouse. The retry stops, waiting for a person, when matching says the job is already finished or
warehouse refuses the number; it stops quietly when the order is no longer `PROCESSING_FULFILLMENT`.
`IAddressDirectory` throws `AddressUnknownException` when identity cannot be asked, so "unknown" and "no
point on the map" stay two answers.

## When the merchant is paid

The escrow is released by order, never by a timer in payment. An order becomes `COMPLETED` once it is
`DELIVERED`, its return window (`KINETIX_RETURN_WINDOW_DAYS`, required, counted from the time the
courier reported) has closed, and no return against it is unresolved (`OPEN` or `GOODS_RECEIVED`).
`OrderCompletionSweeper` does this; an admin can do it early through
`POST /api/v1/orders/{orderNumber}/complete`, under the same rules except the window.

Completing and opening a return both lock the order row (`IOrderRowLock`, `SELECT … FOR UPDATE`)
before they read, so a return cannot be opened against an order whose escrow is being released, and a
completion that waited on a return being opened sees it. A single `UPDATE … WHERE NOT EXISTS` is not
enough: under read committed the subquery is evaluated against the statement's snapshot and misses the
return the other transaction just committed.

`COMPLETED` is committed before payment is asked. The release is then an obligation in
`escrow_releases`, retried with backoff until payment accepts it, the same shape as
`shipping_settlements`. Payment answers a hold it already released as already applied, which is what
the orders it released under its old 48-hour timer get.

## Returns

A return is opened only against a `DELIVERED` order; `COMPLETED` means the window closed and the
merchant was paid. When the goods come back, every line must name an item of the order and no more
than was bought. The buyer gets the share of what the merchant was paid that the returned goods were
worth: `(final_total − final_shipping_fee) × returned value ÷ goods value`, rounded down to the cent.
A voucher is shared out with it, and a full return refunds the merchant's whole share. The shipping
fee is not refunded: the courier delivered.

Refunds go to payment's `RefundGoods` with the key `return:{return_number}`, so a retry cannot refund
twice. A transient failure is retried by the sweeper; a refusal (`FAILED_PRECONDITION`, `NOT_FOUND`,
`INVALID_ARGUMENT`) is not, and leaves the return `GOODS_RECEIVED` with the reason in
`last_refund_error` — and the order uncompleted — until someone resolves it. Goods that come back
for an order no longer `DELIVERED` are recorded with the amount owed, and not sent to an escrow that
can no longer pay it.

An `OPEN` return whose goods never come back keeps the escrow held until an admin rejects it
(`POST /api/v1/orders/returns/{returnNumber}/reject`, with a reason); the order then completes like any
other. It does not lapse on a timer, by decision: the merchant both opens the return and records the
goods coming back, so a timer would let a merchant keep the returned goods and be paid by never
recording them. Paying the merchant instead of refunding the buyer is a judgement, and a person makes
it. A return whose goods came back cannot be rejected — the buyer is owed. Staff see what is stuck at
`GET /api/v1/orders/returns/needs-attention`: returns still `OPEN`, and returns whose refund payment
refused.

## GetOrderDetails names the parties

`merchant_principal_id` is the order's merchant and `driver_principal_id` the courier recorded in
`shipping_settlements` when matching reported the delivery — empty until then. review rates exactly these
two, so a buyer cannot choose whom a review or a rating is about. Both fields also reach the HTTP order
response. `ListOrdersForPrincipal` fills them the same way, looking the couriers up in one query per page.

## Each caller reaches only the RPCs it makes

`KINETIX_GRPC_ALLOWED_PEERS` takes `peer` (every method), `peer@package.Service` (every method of that
service) or `peer@package.Service/Method`, parsed by `PeerAllowList`. Until 2026-10-13 it named services
only, so review — which reads one order — could also call `OrderDelivered`, which pays a courier fee to the
principal it names, or `ReturnGoodsReceived`, which refunds. Infrastructure now grants review
`GetOrderDetails`, matching `OrderDelivered`, search `OrdersChangedSince` and warehouse `FulfillmentPacked`,
`OpenReturn` and `ReturnGoodsReceived`. A listed caller may still use server reflection: it describes the
published contract and nothing else, and the mesh probes (`handshake_matrix.sh`, `trace_test.sh`,
`money_roundtrip.sh`) resolve methods through it. A new call from another service needs its entry before it
ships, or order answers `PermissionDenied`.

## Every transition is in the lifecycle log

`order_lifecycle_events` holds one row for each status an order reaches and each step of its return —
from and to status, the return number and status, who did it (buyer, merchant, driver, admin or the
system, with the principal) and when — written in the same transaction as the change. Change a status only
through `LifecycleLog` (`RecordPlaced`, `ChangeStatus`, `RecordReturnOpened`, `ChangeReturnStatus`); never
assign `Status` directly. Moving an order to the status it already has is not a transition and logs nothing,
which is what a failed checkout meets after the saga has already cancelled the order.

Postgres enforces both properties. Deferred constraint triggers on `orders` and `order_returns` refuse to
commit a status that has no event of its own in the same transaction, so a new code path that forgets the
log fails at its first commit rather than leaving a hole; triggers on the log refuse `UPDATE`, `DELETE` and
`TRUNCATE`. The migration that created the log wrote one `BASELINE` event for every order and return that
already existed: it says what the log first saw, at the time it first saw it, and claims no transition.

The log is the notary's evidence of the order lifecycle (notary PLAN decision 37), read through
`order.v1.OrderLifecycleService/LifecycleEventsSince`. Its cursor is `(transaction_id xid8, sequence)` and it
serves only rows below `pg_snapshot_xmin(pg_current_snapshot())`: a row whose transaction commits after a
later one cannot be skipped. A cursor on a value written before commit loses exactly that row — reproduced on
2026-10-16. The notary needs `notary@order.v1.OrderLifecycleService/LifecycleEventsSince` on the allow list.

## The order feed reads in commit-safe order

`OrdersChangedSince` used to page by `(updated_at, order_number)`, and `updated_at` is set before commit, so
search's read model could lose an order exactly as above. Since 2026-10-19 every order carries
`change_transaction_id xid8`, stamped by the `orders_stamp_change_transaction` trigger on every insert and
update and mapped as generated on add or update, so EF never writes it and reads it back. A cursor that
carries `transaction_id` is served by `OrdersChangedAfterAsync`: orders ordered by
`(change_transaction_id, order_number)`, only below the snapshot's `xmin`, with their line titles read in a
second query so the raw SQL's order survives. A cursor without `transaction_id` is served the old way until
search speaks the new one in production; then `OrdersChangedSinceAsync` and `ix_orders_changed_since` go.

A test that migrates a scratch database to an older migration must write and read that schema's rows in SQL:
the current model names columns the old schema does not have.

## An unknown signing key is looked for at most every ten seconds

`JwksKeyProvider` answers a token's `kid` from the keys it holds. A `kid` it does not hold makes it fetch
identity's JWKS again, but only if the last fetch was at least `UnknownKeyRefreshInterval` (ten seconds, as
the notary uses) ago, and one fetch at a time. Until 2026-10-19 every request carrying an unknown `kid` fetched
the JWKS, synchronously, so a stream of forged tokens with made-up key ids made identity answer once per
request. A key identity publishes is found within ten seconds of the first token that carries it.

## Tokens are read as identity wrote them

`AccessTokenValidation` sets `MapInboundClaims = false`, so `sub` stays `sub` and `role` stays `role`, the
role claim type. Until 2026-10-21 JwtBearer renamed `role` to the long `ClaimTypes.Role` URI while
`RoleClaimType` said `role`: `IsInRole("admin")` was false for every admin, so the saga and return queues,
`complete` and `reject` answered 403 and an admin read of another buyer's order 404. The tests built claims
by hand and never saw it; `OrderApiAuthTests` now sends identity-shaped RS256 tokens through the real handler,
wired by the same `AddAccessTokens` as `Program.cs`.

A token whose key order does not hold, while identity's JWKS could not be fetched, answers 503
`IDENTITY_UNAVAILABLE` (`JwksKeyProvider.CannotVouchFor`): nobody could say whether it was valid. A key
identity does not publish while identity answers is still 401.

## Checkout replays its Idempotency-Key

A blank key is no key: it is stored as NULL, which the unique index on `(customer_principal_id,
idempotency_key)` allows many of. A key is kept with `idempotency_fingerprint`, a SHA-256 of the request
body (`CheckoutFingerprint`), and a repeat answers what the first one became: `PENDING_PAYMENT` is 409
`CHECKOUT_IN_PROGRESS`; a saga that did not complete is 409 `CHECKOUT_ROLLED_BACK` or
`CHECKOUT_ROLLBACK_PENDING` with `replayed: true`; anything else is the order, 201. A different body under
the same key is 422 `IDEMPOTENCY_KEY_REUSED`; rows from before the fingerprint are not compared. A replayed
outage is 409, not 503: the key is spent, and a client that retried a 503 with it would be told 503
forever. Two checkouts racing on one key meet at the insert, before any saga step; the loser catches the
unique violation (`UniqueViolation.On`) and replays the winner.

## A failed checkout says whether everything came back

`CHECKOUT_ROLLED_BACK` (409) only when compensation ended `Compensated`; a saga left `Stuck`, `Abandoned`,
or unwound by another worker is `CHECKOUT_ROLLBACK_PENDING`. When a forward step threw rather than refused
— a dependency did not answer — the answer is 503 `CHECKOUT_DEPENDENCY_UNAVAILABLE` with `rolledBack`.
The 500 body no longer claims nothing happened: it cannot know.

## A completed checkout ends PAID

Clearing the cart after `PAID` is best effort and logged; it never fails the request, which would have
told a charged buyer to retry. If the `PAID` write itself fails after the saga completed, the order would
sit `PENDING_PAYMENT` with the escrow held, and the sweeper looks only at sagas still unwinding.
`CompletedCheckoutFinisher` runs on every `StuckSagaSweeper` pass and marks `PAID` (actor `SYSTEM`) each
`PENDING_PAYMENT` order whose saga has been `Completed` for longer than the sweeper's five-minute grace,
under the order row lock; `ix_orders_pending_payment` keeps that scan small.

It marks `PAID` only a saga that carries every step the runner always takes: at least one `ReserveStock`
(one per cart line), exactly one `CreateEscrowHold` and exactly one `CreateFulfillmentOrder`, all `Done`.
`RedeemVoucher` (only when pricing applied a voucher) and `AllocateFlashSaleStock` (one per flash-sale line)
are optional. Until fulfillment existed a successful checkout never set `PAID` and created no warehouse task,
so production kept `ORD-20260917-E4C9664A`, from v0.1.0: `Completed` with `ReserveStock` and
`CreateEscrowHold` only, the buyer's money held and nothing to pack. Marking it `PAID` would leave it there for
good. Such a saga is flagged instead (`needs_attention_at`, code `COMPLETED_WITHOUT_FULFILLMENT`, the reason in
`failure_reason`), logged once, and left out of every later pass; its order stays `PENDING_PAYMENT`. It shows in
`GET /api/v1/orders/sagas/needs-attention` with its held steps and their remedies. The sagaless canceller skips it
(it has a saga row), and the compensation sweep never claims a `Completed` saga.

An order can also sit `PENDING_PAYMENT` with no saga row at all, when the process died between inserting
the order and inserting its saga; its key then replayed `CHECKOUT_IN_PROGRESS` forever.
`SagalessCheckoutCanceller`, on the same sweep, cancels such an order (actor `SYSTEM`, under the order row
lock) once it is 15 minutes old: three times the sweeper's five-minute rule for a checkout that has stopped,
and far beyond the one INSERT that separates the two rows (Npgsql's 30-second command timeout, 25 seconds of
shutdown budget) and beyond a whole checkout, whose calls each have a 5-second gRPC deadline and the
geocode its own. Nothing was charged: every saga step is recorded before its call goes out, and steps hang
off the saga row, so no saga row means no call was made. It cancels only when no saga row exists, and in the
same transaction inserts a `Compensated` saga carrying the reason. That row makes the key replay as rolled
back, and the unique index on `checkout_sagas.order_number` decides a race with a checkout still inserting
its saga: whichever insert commits first wins, and a cancel that loses leaves the order alone.

Every gRPC client turns a fault that is not an `RpcException` — the mesh handler failing before the call goes
out — into the same "could not ask" answer as an outage (503, or a deferred retry for the courier
dispatcher), as `ShippingGrpcClient` always did.

## What checkout refuses before it asks anyone

Lengths are the columns' (`Order.*MaxLength`: address 500, name 128, phone 32, voucher 50, key 100) and are
400 `FIELD_TOO_LONG`, so Postgres never turns them into a 500 after geocoding and pricing. A cart line holds
1 to 999 (`CartItem.MaxQuantity`), at add-to-cart and again at checkout, because a cart may predate the
check. A buyer cannot buy a product whose merchant principal is their own principal (the user's ruling,
2026-10-21): 422 `OWN_PRODUCT_NOT_PURCHASABLE` at add-to-cart and at checkout. A total of exactly zero is
422 `ORDER_TOTAL_NOT_CHARGEABLE` before any saga step: payment refuses to hold nothing. A shipping-only
total is above zero and is charged.

Pricing's `InvalidArgument`, `FailedPrecondition`, `NotFound` and `OutOfRange` are its refusal, 422
`PRICING_REFUSED` with its reason; every other failure is 503 `PRICING_UNAVAILABLE`. Catalog's `NotFound`
and `InvalidArgument` are `PRODUCT_NOT_IN_CATALOG`; a product it names no merchant for is 422
`PRODUCT_HAS_NO_MERCHANT`.

## The cart is edited by compare-and-set

`RedisCartStore` keeps the cart in the hash `IDistributedCache` used (`KinetixOrder:cart:principal:{id}`,
field `data`), so carts written before 2026-10-21 still read, and adds a `ver` field. A write is a Lua
script that replaces `data` and bumps `ver` only if `ver` is still what the edit read; `CartService` re-reads
and re-applies a refused edit up to `MaxEditAttempts` (10) times, then answers 409 `CART_BUSY`. A
read-modify-write with no check lost concurrent adds: twenty parallel adds of one left one. Optimistic
rather than a lock: no lock to leak past its expiry while a catalog call is slow, and a refused write costs
one re-read. Ten attempts are enough for ten concurrent edits of one cart, because each refusal means another
edit landed. An unreachable Redis is 503 `CART_UNAVAILABLE`.

## The 201 Location is relative

Kong does not preserve the host, so `CreatedAtAction` named `kinetix-order-service:8001`. Checkout answers
`Location: /api/v1/orders/{id}`, the path Kong routes unchanged.

## The suite runs in CI, against Postgres

Until 2026-10-16 neither CI ran `dotnet test`. Both now do, with a Postgres service and
`KINETIX_TEST_POSTGRES`. The Postgres tests share one migrated database through the `postgres` xunit
collection (`PostgresDatabase`, `PostgresCollection`), which also runs them one at a time: run separately
against a fresh database, each class migrated it at once and they failed on each other's half-built schema.
They still return early without `KINETIX_TEST_POSTGRES`, so `PostgresIsConfiguredTests` fails when `CI` is set
and the database is not. Locally, start a Postgres on a free port and set the variable; a port another
container already holds makes the tests talk to that database instead. The cart's Redis tests work the
same way with `KINETIX_TEST_REDIS` (`host:port`) and `RedisIsConfiguredTests`.
