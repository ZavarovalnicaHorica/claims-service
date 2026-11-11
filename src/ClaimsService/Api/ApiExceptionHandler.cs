using System.Text.Json;
using Horica.ClaimsService.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Horica.ClaimsService.Api;

/// <summary>Turns expected errors (400, 403, 404, 409, 503) into RFC 7807 problem responses.</summary>
public sealed class ApiExceptionHandler : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        int status;
        string title;
        string detail;

        if (exception is ApiException apiException)
        {
            status = apiException.StatusCode;
            title = apiException.Title;
            detail = apiException.Message;
        }
        else if (exception is DbUpdateConcurrencyException)
        {
            status = 409;
            title = "Conflict";
            detail = "The claim was changed in the meantime. Reload it and try again.";
        }
        else
        {
            // Unexpected error: let the default handler produce a 500 response.
            return false;
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(
            problem,
            (JsonSerializerOptions?)null,
            "application/problem+json",
            cancellationToken);

        return true;
    }
}
