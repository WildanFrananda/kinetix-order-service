namespace Kinetix.OrderService.Application.Checkout;

public static class ShippingRateCardProbe {
    public const double Latitude = 0.0;
    public const double Longitude = 0.0;
    public const long WeightGrams = 0L;
    public const double ExpectedDistanceKm = 0.0;

    public const string AvailabilityBasis =
        "matching's rate card evaluated at 0 km and 0 kg, because order cannot yet state this "
      + "delivery's route or its parcel weight; it is not a verdict about this delivery";
}
