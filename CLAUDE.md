# CLAUDE.md — kinetix-order-service

The workspace `AGENTS.md` holds the stack and the verification commands. This file holds decisions
that the code states but cannot explain.

## Order items are priced by pricing

`OrderService.PricedItems` builds each item from pricing's line for it — the final unit price and line
total the buyer is charged, flash sale included — and the title from catalog. Nothing on an item comes
from the cart. A line pricing did not price, a quantity it priced differently, or lines that do not add
up to its subtotal refuse the checkout. Returns are refunded from these amounts, so they must be what
was paid.

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
