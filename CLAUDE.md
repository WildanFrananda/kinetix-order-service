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

## The suite runs in CI, against Postgres

Until 2026-10-16 neither CI ran `dotnet test`. Both now do, with a Postgres service and
`KINETIX_TEST_POSTGRES`. The Postgres tests share one migrated database through the `postgres` xunit
collection (`PostgresDatabase`, `PostgresCollection`), which also runs them one at a time: run separately
against a fresh database, each class migrated it at once and they failed on each other's half-built schema.
They still return early without `KINETIX_TEST_POSTGRES`, so `PostgresIsConfiguredTests` fails when `CI` is set
and the database is not. Locally, start a Postgres on a free port and set the variable; a port another
container already holds makes the tests talk to that database instead.
