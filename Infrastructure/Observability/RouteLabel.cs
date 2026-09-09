using System.Text;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Kinetix.OrderService.Infrastructure.Observability;

public static class RouteLabel {
    public const string Unmatched = "unmatched";

    public static string Of(HttpContext context) {
        if (context.GetEndpoint() is not RouteEndpoint endpoint) {
            return Unmatched;
        }

        return Of(endpoint.RoutePattern);
    }

    public static string Of(RoutePattern pattern) {
        if (pattern.PathSegments.Count == 0) {
            return "/";
        }

        var label = new StringBuilder();

        foreach (var segment in pattern.PathSegments) {
            label.Append('/');

            foreach (var part in segment.Parts) {
                switch (part) {
                    case RoutePatternLiteralPart literal:
                        label.Append(literal.Content);
                        break;
                    case RoutePatternSeparatorPart separator:
                        label.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        label.Append(parameter.IsCatchAll ? "{*" : "{")
                             .Append(parameter.Name)
                             .Append('}');
                        break;
                }
            }
        }

        return label.ToString();
    }
}
