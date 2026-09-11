namespace nashira_backend.Exceptions;

// Base for every exception the HTTP surface translates to a ProblemDetails
// response. Services throw these instead of returning ad-hoc error payloads;
// DomainExceptionHandler (Services/Errors) maps them to the right status code.
// Status is set per-subtype; Code becomes problem+json extensions.code.
public abstract class DomainException : Exception
{
    public string Code { get; }
    public int Status { get; }

    protected DomainException(string code, string message, int status) : base(message)
    {
        Code = code;
        Status = status;
    }
}

public sealed class ValidationException : DomainException
{
    public ValidationException(string message, string code = "validation_error")
        : base(code, message, StatusCodes.Status400BadRequest) { }
}

public sealed class NotFoundException : DomainException
{
    public NotFoundException(string message, string code = "not_found")
        : base(code, message, StatusCodes.Status404NotFound) { }
}

public sealed class ConflictException : DomainException
{
    public ConflictException(string message, string code = "conflict")
        : base(code, message, StatusCodes.Status409Conflict) { }
}

public sealed class UnauthorizedException : DomainException
{
    public UnauthorizedException(string message = "unauthorized", string code = "unauthorized")
        : base(code, message, StatusCodes.Status401Unauthorized) { }
}

public sealed class ForbiddenException : DomainException
{
    public ForbiddenException(string message = "forbidden", string code = "forbidden")
        : base(code, message, StatusCodes.Status403Forbidden) { }
}

// 412: a gate precondition is not met (promotion gates: simulation_missing/
// simulation_failed/simulation_stale/approval_required). The code is contract-visible.
public sealed class PreconditionFailedException : DomainException
{
    public PreconditionFailedException(string message, string code = "precondition_failed")
        : base(code, message, StatusCodes.Status412PreconditionFailed) { }
}
