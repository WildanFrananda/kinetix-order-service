using Grpc.Core;
using CommonProto = global::Common.V1;
using Fleet.V1;
using Kinetix.OrderService.Application.Checkout;
using Kinetix.OrderService.Application.Exceptions;
using Kinetix.OrderService.Application.Ports;
using Kinetix.OrderService.Domain.Entities;
using DomainStatus = Kinetix.OrderService.Domain.Enums.OrderStatus;
using OrderEntity = Kinetix.OrderService.Domain.Entities.Order;

namespace Kinetix.OrderService.Application.Fulfillment;

public class CourierDispatcher(
    CourierTelemetryService.CourierTelemetryServiceClient courierClient,
    IAddressDirectory addresses,
    IFulfillmentClient fulfillment,
    ILogger<CourierDispatcher> logger
) : ICourierDispatcher {
    private const string OrderAlreadyFinished = "ORDER_ALREADY_FINISHED";

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    private readonly CourierTelemetryService.CourierTelemetryServiceClient _courierClient = courierClient;
    private readonly IAddressDirectory _addresses = addresses;
    private readonly IFulfillmentClient _fulfillment = fulfillment;
    private readonly ILogger<CourierDispatcher> _logger = logger;

    public async Task AttemptAsync(CourierDispatch dispatch, OrderEntity order) {
        dispatch.Attempts += 1;
        dispatch.UpdatedAt = DateTime.UtcNow;

        if (order.Status is not DomainStatus.PROCESSING_FULFILLMENT) {
            Close(dispatch, $"the order is {order.Status}, so there is nothing left to dispatch");
            return;
        }

        if (dispatch.DispatchedAt is null && !await DispatchAsync(dispatch, order)) {
            return;
        }

        if (dispatch.AwbRecordedAt is null && !await RecordAwbAsync(dispatch)) {
            return;
        }

        dispatch.NextAttemptAt = null;
        dispatch.LastError = null;
    }

    private async Task<bool> DispatchAsync(CourierDispatch dispatch, OrderEntity order) {
        MapPoint? pickup;
        MapPoint? delivery;

        try {
            pickup = await _addresses.PickupPointAsync(dispatch.MerchantPrincipalId);
            delivery = await _addresses.DeliveryPointAsync(order.CustomerPrincipalId);
        } catch (AddressUnknownException e) {
            Defer(dispatch, $"{e.Message}: {e.InnerException?.Message}");
            return false;
        }

        if (pickup is null || delivery is null) {
            var missing = pickup is null && delivery is null ? "neither end"
                : pickup is null ? "the merchant's store" : "the delivery address";
            Defer(
                dispatch,
                $"{missing} has no point on the map; identity places an address when it is saved"
            );

            return false;
        }

        DispatchCourierResponse response;
        try {
            response = await _courierClient.DispatchCourierAsync(new DispatchCourierRequest {
                MerchantPrincipalId = dispatch.MerchantPrincipalId,
                OrderId = order.Id.ToString(),
                OrderNumber = order.OrderNumber,
                PickupPoint = new CommonProto.GeoPoint {
                    Latitude = pickup.Latitude,
                    Longitude = pickup.Longitude,
                },
                DeliveryPoint = new CommonProto.GeoPoint {
                    Latitude = delivery.Latitude,
                    Longitude = delivery.Longitude,
                },
            });
        } catch (RpcException e) {
            Defer(dispatch, $"the fleet could not be reached: {e.Status.Detail}");
            return false;
        }

        if (!response.Success) {
            var code = response.Error?.ErrorCode ?? "DISPATCH_REFUSED";
            var reason = $"{code}: {response.Error?.Message ?? "no reason given"}";

            if (code == OrderAlreadyFinished) {
                Stop(dispatch, $"the fleet refused: {reason}");
            } else {
                Defer(dispatch, $"no courier yet: {reason}");
            }

            return false;
        }

        dispatch.DispatchRef = response.DispatchRef;
        dispatch.AwbNumber = response.AwbNumber;
        dispatch.DispatchedAt = DateTime.UtcNow;

        _logger.LogInformation(
            "{Order} was dispatched as {DispatchRef} on attempt {Attempt}",
            order.OrderNumber,
            dispatch.DispatchRef,
            dispatch.Attempts
        );

        return true;
    }

    private async Task<bool> RecordAwbAsync(CourierDispatch dispatch) {
        StepResult recorded;
        try {
            recorded = await _fulfillment.RecordCourierAwbAsync(
                dispatch.MerchantPrincipalId,
                dispatch.FulfillmentTaskId,
                dispatch.OrderNumber,
                dispatch.AwbNumber ?? string.Empty
            );
        } catch (RpcException e) {
            Defer(
                dispatch,
                $"the courier is assigned and the warehouse could not be told the tracking number: {e.Status.Detail}"
            );

            return false;
        }

        if (!recorded.Success) {
            Stop(
                dispatch,
                $"the courier is assigned and the warehouse refused the tracking number: {recorded.Detail}"
            );

            return false;
        }

        dispatch.AwbRecordedAt = DateTime.UtcNow;
        return true;
    }

    private void Defer(CourierDispatch dispatch, string reason) {
        var backoff = TimeSpan.FromMinutes(Math.Min(dispatch.Attempts, MaxBackoff.TotalMinutes));

        dispatch.LastError = Trim(reason);
        dispatch.NextAttemptAt = DateTime.UtcNow.Add(backoff);

        _logger.LogWarning(
            "{Order} is not fully dispatched after {Attempts} attempt(s): {Reason}. Next attempt in {Backoff}.",
            dispatch.OrderNumber,
            dispatch.Attempts,
            dispatch.LastError,
            backoff
        );
    }

    private void Stop(CourierDispatch dispatch, string reason) {
        dispatch.LastError = Trim(reason);
        dispatch.NextAttemptAt = null;

        _logger.LogError(
            "{Order} will not be dispatched again without a person: {Reason}",
            dispatch.OrderNumber,
            dispatch.LastError
        );
    }

    private void Close(CourierDispatch dispatch, string reason) {
        dispatch.LastError = Trim(reason);
        dispatch.NextAttemptAt = null;

        _logger.LogInformation("{Order} is no longer dispatched: {Reason}", dispatch.OrderNumber, reason);
    }

    private static string Trim(string reason) => reason.Length <= 500 ? reason : reason[..500];
}
