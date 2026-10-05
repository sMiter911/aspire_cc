namespace TodoApi.Middleware;

/// <summary>Same error envelope as the Spring Boot API, so the React client handles both identically.</summary>
public record ApiError(
    DateTimeOffset Timestamp,
    int Status,
    string Code,
    string Message,
    string Path,
    IReadOnlyList<FieldError> Errors)
{
    public static ApiError Create(int status, string code, string message, string path,
        IReadOnlyList<FieldError>? errors = null) =>
        new(DateTimeOffset.UtcNow, status, code, message, path, errors ?? []);

    public static ApiError ForStatus(int status, string path) => status switch
    {
        400 => Create(status, "BAD_REQUEST", "Bad request", path),
        401 => Create(status, "UNAUTHENTICATED", "Authentication required", path),
        403 => Create(status, "ACCESS_DENIED", "You do not have permission to perform this action", path),
        404 => Create(status, "NOT_FOUND", "Resource not found", path),
        405 => Create(status, "METHOD_NOT_ALLOWED", "Method not allowed", path),
        413 => Create(status, "PAYLOAD_TOO_LARGE", "Request body too large", path),
        415 => Create(status, "UNSUPPORTED_MEDIA_TYPE", "Unsupported media type", path),
        _ => Create(status, status >= 500 ? "INTERNAL_ERROR" : "ERROR", "Request failed", path),
    };
}

public record FieldError(string Field, string Message);
