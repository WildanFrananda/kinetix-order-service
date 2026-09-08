namespace Kinetix.OrderService.Application.Checkout;

public static class CompensationFailureCode {
    public const string DownstreamUnavailable = "DOWNSTREAM_UNAVAILABLE";
    public const string DownstreamRefused = "DOWNSTREAM_REFUSED";
    public const string AttemptsExhausted = "ATTEMPTS_EXHAUSTED";
    public const string NoCompensationDefined = "NO_COMPENSATION_DEFINED";
    public const string EscrowAbsent = "ESCROW_ABSENT";
    public const string EscrowMissingAfterHold = "ESCROW_MISSING_AFTER_HOLD";
    public const string EscrowRefusedButHeld = "ESCROW_REFUSED_BUT_HELD";
    public const string EscrowRefundUnconfirmed = "ESCROW_REFUND_UNCONFIRMED";
    public const string EscrowAlreadyReleased = "ESCROW_ALREADY_RELEASED";
    public const string EscrowUnexpectedStatus = "ESCROW_UNEXPECTED_STATUS";
}
